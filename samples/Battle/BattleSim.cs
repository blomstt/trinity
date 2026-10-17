using System.Diagnostics;
using System.Numerics;
using Trinity;
using Trinity.Graphics;

namespace BattleSample;

/// <summary>
/// Two armies of spearmen. Data is kept as plain arrays (one per field) so the update loops stay
/// cache-friendly, and the per-soldier work runs as BRUTAL jobs across all cores.
/// </summary>
public sealed unsafe class BattleSim
{
    // Tuning, in metres and seconds
    const float CellSize = 2f;
    const int GridCells = 512; // 1 km x 1 km battlefield
    const float WorldHalf = GridCells * CellSize / 2;
    const float MarchSpeed = 1.7f;
    const float ChargeSpeed = 3.2f;
    const float SpearReach = 1.7f;
    const float EngageRadius = 7f;
    const float Personal = 0.85f; // soldiers keep this far apart
    const float AttackTime = 0.9f;
    const float Damage = 34f;
    const float HitChance = 0.55f;
    const float DeathTime = 1.1f;

    public int Count { get; private set; }
    public int AliveBlue { get; private set; }
    public int AliveRed { get; private set; }
    public double LastSimMs { get; private set; }
    public int NearCount { get; private set; }
    public int FarCount { get; private set; }

    byte[] _team = [];
    float[] _x = [], _z = [], _nx = [], _nz = [], _vx = [], _vz = [];
    float[] _px = [], _pz = []; // positions before the last step, to blend between steps when drawing
    float[] _yaw = [], _hp = [], _walk = [], _attack = [], _death = [];
    int[] _damage = []; // fixed-point damage received this frame, added atomically by attackers
    SoldierInstance[] _staging = []; // this frame's soldiers before culling
    byte[] _bucket = []; // 0 = close-up, 1 = distant, 2 = off screen
    Frustum _frustum;
    Vector3 _eye;
    float _detailDistance;

    // Grid of living soldiers, rebuilt every frame with a counting sort
    readonly int[] _cellStart = new int[GridCells * GridCells + 1];
    readonly int[] _fill = new int[GridCells * GridCells];
    int[] _cellItems = [];

    Vector2 _blueCentre, _redCentre;
    float _dt;
    uint _frame;
    SoldierInstance* _out;

    // Work is split into the job system's chunks; each chunk counts what it saw, then copies at its offset
    readonly Jobs _jobs;
    readonly int[] _nearCounts, _farCounts, _nearOffsets, _farOffsets;
    readonly Jobs.RangeBody _simulate, _classify, _copy;
    float _alpha;

    /// <summary>Metres of open ground between the two front ranks at the start.</summary>
    public float Gap = 90f;

    public BattleSim(Jobs jobs, int perSide, float gap)
    {
        _jobs = jobs;
        _nearCounts = new int[jobs.ChunkCount];
        _farCounts = new int[jobs.ChunkCount];
        _nearOffsets = new int[jobs.ChunkCount];
        _farOffsets = new int[jobs.ChunkCount];
        _simulate = UpdateRange;
        _classify = ClassifyChunk;
        _copy = CopyChunk;
        Gap = gap;
        Spawn(perSide);
    }

    public void Spawn(int perSide)
    {
        Count = perSide * 2;
        _team = new byte[Count];
        _x = new float[Count]; _z = new float[Count]; _nx = new float[Count]; _nz = new float[Count];
        _px = new float[Count]; _pz = new float[Count];
        _vx = new float[Count]; _vz = new float[Count];
        _yaw = new float[Count]; _hp = new float[Count]; _walk = new float[Count];
        _attack = new float[Count]; _death = new float[Count];
        _damage = new int[Count];
        _cellItems = new int[Count];
        _staging = new SoldierInstance[Count];
        _bucket = new byte[Count];

        // Two blocks of infantry facing each other across 90 m of open field
        var columns = (int)MathF.Ceiling(MathF.Sqrt(perSide * 3f));
        for (var i = 0; i < Count; i++)
        {
            var team = i < perSide ? 0 : 1;
            var k = i - team * perSide;
            var col = k % columns;
            var row = k / columns;
            var side = team == 0 ? -1f : 1f;
            _team[i] = (byte)team;
            _x[i] = (col - columns / 2f) * 1.3f + (Hash(i, 1) - 0.5f) * 0.4f;
            _z[i] = side * (Gap / 2 + row * 1.3f) + (Hash(i, 2) - 0.5f) * 0.4f;
            _yaw[i] = team == 0 ? 0f : MathF.PI;
            _px[i] = _x[i];
            _pz[i] = _z[i];
            _hp[i] = 100f;
            _walk[i] = Hash(i, 3) * MathF.Tau;
            _attack[i] = -1f;
        }
        _frame = 0;
    }

    /// <summary>Advances the battle by one step. Call at a fixed rate.</summary>
    public void Simulate(float dt)
    {
        var timer = Stopwatch.StartNew();
        _dt = Math.Min(dt, 1f / 20f);
        _frame++;

        BuildGrid();
        _jobs.For(Count, _simulate);

        // Positions move in one go so every chunk read the same state; then wounds land
        (_x, _nx) = (_nx, _x);
        (_z, _nz) = (_nz, _z);
        for (var i = 0; i < Count; i++)
        {
            if (_damage[i] == 0)
                continue;
            _hp[i] -= _damage[i] / 1000f;
            _damage[i] = 0;
            if (_hp[i] <= 0 && _death[i] == 0)
                _death[i] = 0.0001f;
        }
        LastSimMs = timer.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Writes the soldiers the camera can see into <paramref name="output"/>: <see cref="NearCount"/>
    /// close-up ones first, then <see cref="FarCount"/> distant ones. Positions are blended
    /// <paramref name="alpha"/> of the way from the previous step to the latest. Call every frame.
    /// </summary>
    public void Gather(Span<SoldierInstance> output, in Frustum frustum, Vector3 eye, float detailDistance, float alpha)
    {
        _frustum = frustum;
        _eye = eye;
        _detailDistance = detailDistance;
        _alpha = alpha;
        fixed (SoldierInstance* outPtr = output)
        {
            _out = outPtr;
            Array.Clear(_nearCounts);
            Array.Clear(_farCounts);
            _jobs.For(Count, _classify);

            // Where each chunk's soldiers go: all close-ups first, then all distant ones
            int near = 0, far = 0;
            for (var c = 0; c < _nearCounts.Length; c++)
            {
                _nearOffsets[c] = near;
                near += _nearCounts[c];
            }
            for (var c = 0; c < _farCounts.Length; c++)
            {
                _farOffsets[c] = near + far;
                far += _farCounts[c];
            }
            NearCount = near;
            FarCount = far;

            _jobs.For(Count, _copy);
            _out = null;
        }
    }

    void ClassifyChunk(int start, int end, int chunk)
    {
        int near = 0, far = 0;
        var detail2 = _detailDistance * _detailDistance;
        for (var i = start; i < end; i++)
        {
            // A soldier with his spear fits in a 2 m sphere around his chest
            var centre = new Vector3(_x[i], 0.9f, _z[i]);
            byte b = !_frustum.Contains(centre, 2f) ? (byte)2 : Vector3.DistanceSquared(centre, _eye) < detail2 ? (byte)0 : (byte)1;
            _bucket[i] = b;
            near += b == 0 ? 1 : 0;
            far += b == 1 ? 1 : 0;
        }
        _nearCounts[chunk] = near;
        _farCounts[chunk] = far;
    }

    void CopyChunk(int start, int end, int chunk)
    {
        int near = _nearOffsets[chunk], far = _farOffsets[chunk];
        for (var i = start; i < end; i++)
        {
            var b = _bucket[i];
            if (b == 2)
                continue;
            var soldier = _staging[i];
            soldier.Place.X = float.Lerp(_px[i], _x[i], _alpha);
            soldier.Place.Y = float.Lerp(_pz[i], _z[i], _alpha);
            if (b == 0)
                _out[near++] = soldier;
            else
                _out[far++] = soldier;
        }
    }

    void BuildGrid()
    {
        Array.Clear(_cellStart);
        int blue = 0, red = 0;
        Vector2 blueSum = default, redSum = default;
        for (var i = 0; i < Count; i++)
        {
            if (_death[i] > 0)
                continue;
            _cellStart[Cell(_x[i], _z[i]) + 1]++;
            if (_team[i] == 0) { blue++; blueSum += new Vector2(_x[i], _z[i]); }
            else { red++; redSum += new Vector2(_x[i], _z[i]); }
        }
        for (var c = 0; c < GridCells * GridCells; c++)
            _cellStart[c + 1] += _cellStart[c];
        Array.Copy(_cellStart, _fill, _fill.Length);
        for (var i = 0; i < Count; i++)
            if (_death[i] == 0)
                _cellItems[_fill[Cell(_x[i], _z[i])]++] = i;

        AliveBlue = blue;
        AliveRed = red;
        _blueCentre = blue > 0 ? blueSum / blue : _blueCentre;
        _redCentre = red > 0 ? redSum / red : _redCentre;
    }

    static int Cell(float x, float z)
    {
        var cx = Math.Clamp((int)((x + WorldHalf) / CellSize), 0, GridCells - 1);
        var cz = Math.Clamp((int)((z + WorldHalf) / CellSize), 0, GridCells - 1);
        return cz * GridCells + cx;
    }

    void UpdateRange(int start, int end, int chunk)
    {
        var dt = _dt;
        var reach = (int)MathF.Ceiling(EngageRadius / CellSize);
        for (var i = start; i < end; i++)
        {
            var x = _x[i];
            var z = _z[i];
            _px[i] = x;
            _pz[i] = z;

            if (_death[i] > 0)
            {
                _death[i] = Math.Min(1f, _death[i] + dt / DeathTime);
                _nx[i] = x;
                _nz[i] = z;
                Write(i, x, z, 0);
                continue;
            }

            // Look around: nearest enemy for the fight, close friends and foes for spacing
            var team = _team[i];
            float bestDist2 = EngageRadius * EngageRadius;
            var best = -1;
            float sepX = 0, sepZ = 0;
            var ccx = Math.Clamp((int)((x + WorldHalf) / CellSize), 0, GridCells - 1);
            var ccz = Math.Clamp((int)((z + WorldHalf) / CellSize), 0, GridCells - 1);
            for (var gz = Math.Max(0, ccz - reach); gz <= Math.Min(GridCells - 1, ccz + reach); gz++)
            for (var gx = Math.Max(0, ccx - reach); gx <= Math.Min(GridCells - 1, ccx + reach); gx++)
            {
                var c = gz * GridCells + gx;
                var near = Math.Abs(gx - ccx) <= 1 && Math.Abs(gz - ccz) <= 1;
                for (var k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                {
                    var o = _cellItems[k];
                    if (o == i)
                        continue;
                    var dx = _x[o] - x;
                    var dz = _z[o] - z;
                    var d2 = dx * dx + dz * dz;
                    if (near && d2 < Personal * Personal && d2 > 1e-6f)
                    {
                        var d = MathF.Sqrt(d2);
                        var push = (Personal - d) / d;
                        sepX -= dx * push;
                        sepZ -= dz * push;
                    }
                    if (_team[o] != team && d2 < bestDist2)
                    {
                        bestDist2 = d2;
                        best = o;
                    }
                }
            }

            // Decide: fight whoever is in reach, close in on the nearest foe, or march at the enemy army
            float wantX, wantZ, faceX, faceZ;
            var fighting = false;
            if (best >= 0)
            {
                var dx = _x[best] - x;
                var dz = _z[best] - z;
                var d = MathF.Sqrt(bestDist2);
                faceX = dx / d;
                faceZ = dz / d;
                if (d <= SpearReach)
                {
                    fighting = true;
                    wantX = wantZ = 0;
                }
                else
                {
                    wantX = faceX * ChargeSpeed;
                    wantZ = faceZ * ChargeSpeed;
                }

                if (fighting)
                {
                    var before = _attack[i] < 0 ? 0 : _attack[i];
                    var after = before + dt / AttackTime;
                    // The jab lands at 75% of the swing
                    if (before < 0.75f && after >= 0.75f && Hash(i, _frame) < HitChance)
                        Interlocked.Add(ref _damage[best], (int)(Damage * 1000));
                    _attack[i] = after >= 1f ? after - 1f : after;
                }
                else
                {
                    _attack[i] = -1f;
                }
            }
            else
            {
                var target = team == 0 ? _redCentre : _blueCentre;
                var ahead = target.Y > z ? 1f : -1f;
                var drift = Math.Clamp((target.X - x) * 0.01f, -0.25f, 0.25f);
                var d = MathF.Sqrt(1f + drift * drift);
                faceX = drift / d;
                faceZ = ahead / d;
                wantX = faceX * MarchSpeed;
                wantZ = faceZ * MarchSpeed;
                _attack[i] = -1f;
            }

            wantX += sepX * 4f;
            wantZ += sepZ * 4f;
            var blend = 1f - MathF.Exp(-dt * 6f);
            _vx[i] += (wantX - _vx[i]) * blend;
            _vz[i] += (wantZ - _vz[i]) * blend;

            var speed = MathF.Sqrt(_vx[i] * _vx[i] + _vz[i] * _vz[i]);
            _walk[i] += speed * dt * 3.2f;
            var yawWanted = fighting || speed < 0.3f ? MathF.Atan2(faceX, faceZ) : MathF.Atan2(_vx[i], _vz[i]);
            _yaw[i] = TurnTowards(_yaw[i], yawWanted, dt * 6f);

            _nx[i] = Math.Clamp(x + _vx[i] * dt, -WorldHalf + 1, WorldHalf - 1);
            _nz[i] = Math.Clamp(z + _vz[i] * dt, -WorldHalf + 1, WorldHalf - 1);
            Write(i, _nx[i], _nz[i], speed);
        }
    }

    void Write(int i, float x, float z, float speed)
    {
        _staging[i] = new SoldierInstance
        {
            Place = new Vector4(x, z, _yaw[i], _team[i]),
            Anim = new Vector4(_walk[i], _attack[i], _death[i], speed),
        };
    }

    static float TurnTowards(float from, float to, float maxStep)
    {
        var diff = MathF.IEEERemainder(to - from, MathF.Tau);
        return from + Math.Clamp(diff, -maxStep, maxStep);
    }

    /// <summary>Cheap deterministic noise in 0..1, so runs repeat exactly.</summary>
    static float Hash(int a, uint b)
    {
        var h = (uint)a * 0x9E3779B1u ^ b * 0x85EBCA77u;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }
}
