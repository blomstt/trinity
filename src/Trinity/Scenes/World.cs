using System.Numerics;

namespace Trinity.Scenes;

/// <summary>
/// An entity's runtime identity: a slot index and that slot's generation. When an entity is destroyed
/// its slot's generation goes up, so an old id can never reach whatever reuses the slot. Runtime only:
/// files store persistent ids (<see cref="World.PersistentId"/>) instead. The default value is "none".
/// </summary>
public readonly record struct EntityId(int Index, int Generation)
{
    public static readonly EntityId None = default;
    public bool IsNone => Generation == 0;
    public override string ToString() => IsNone ? "none" : $"#{Index}.{Generation}";
}

/// <summary>Untyped access to a component store, for the world's bookkeeping.</summary>
public interface IComponentStore
{
    bool Has(EntityId e);
    bool Remove(EntityId e);
    int Count { get; }
}

/// <summary>
/// One component type's data for every entity that has it: a sparse set, so components sit contiguously
/// (<see cref="Components"/> and <see cref="Entities"/> line up) and lookup by entity is a direct index.
/// An entity has at most one of each type. Removing swaps the last component into the gap.
/// </summary>
public sealed class ComponentStore<T> : IComponentStore where T : struct
{
    int[] _sparse = new int[64]; // entity index -> dense index + 1 (0: none)
    T[] _dense = new T[16];
    EntityId[] _owners = new EntityId[16];
    int _count;
    T _missing;

    public int Count => _count;

    /// <summary>The components, for iteration in place (ref access, no copies).</summary>
    public Span<T> Components => _dense.AsSpan(0, _count);

    /// <summary>Which entity owns each component in <see cref="Components"/>.</summary>
    public ReadOnlySpan<EntityId> Entities => _owners.AsSpan(0, _count);

    public bool Has(EntityId e) => e.Index < _sparse.Length && _sparse[e.Index] is var d && d != 0 && _owners[d - 1] == e;

    public ref T Add(EntityId e, in T value)
    {
        if (Has(e))
            throw new InvalidOperationException($"entity {e} already has a {typeof(T).Name}");
        if (e.Index >= _sparse.Length)
            Array.Resize(ref _sparse, Math.Max(e.Index + 1, _sparse.Length * 2));
        if (_count == _dense.Length)
        {
            Array.Resize(ref _dense, _count * 2);
            Array.Resize(ref _owners, _count * 2);
        }
        _dense[_count] = value;
        _owners[_count] = e;
        _sparse[e.Index] = ++_count;
        return ref _dense[_count - 1];
    }

    public ref T Get(EntityId e)
    {
        if (!Has(e))
            throw new InvalidOperationException($"entity {e} has no {typeof(T).Name}");
        return ref _dense[_sparse[e.Index] - 1];
    }

    /// <summary>The component, or a scratch value with <paramref name="found"/> false (writes to it go nowhere).</summary>
    public ref T TryGet(EntityId e, out bool found)
    {
        found = Has(e);
        if (found)
            return ref _dense[_sparse[e.Index] - 1];
        _missing = default;
        return ref _missing;
    }

    public bool Remove(EntityId e)
    {
        if (!Has(e))
            return false;
        var d = _sparse[e.Index] - 1;
        var last = _count - 1;
        if (d != last)
        {
            _dense[d] = _dense[last];
            _owners[d] = _owners[last];
            _sparse[_owners[d].Index] = d + 1;
        }
        _dense[last] = default;
        _owners[last] = default;
        _sparse[e.Index] = 0;
        _count--;
        return true;
    }
}

/// <summary>
/// The entities of a running (or edited) scene: identities, the parent/child hierarchy, local
/// transforms with cached world matrices, names, persistent ids, and component stores.
/// <para>
/// Hierarchy and transforms live in parallel arrays indexed by entity slot; siblings are linked lists,
/// so child order is kept without allocating per node. World matrices are cached: changing a node's
/// local transform marks it and its descendants dirty (stopping at nodes already dirty, since a dirty
/// node's descendants are always dirty), and a world matrix is recomputed only when asked for and dirty.
/// </para>
/// <para>
/// While the world is being iterated (<see cref="BeginIteration"/>), destroying entities and removing
/// components is deferred until the iteration ends, so loops over stores and children stay valid.
/// Single-threaded: callers synchronise if they share a world across threads.
/// </para>
/// </summary>
public sealed class World
{
    const int NoIndex = -1;

    int[] _generation = new int[64];
    bool[] _alive = new bool[64];
    int _highWater;
    readonly Stack<int> _free = new();
    int _count;

    int[] _parent = new int[64], _firstChild = new int[64], _lastChild = new int[64], _next = new int[64], _prev = new int[64];
    int _firstRoot = NoIndex, _lastRoot = NoIndex;

    Vector3[] _position = new Vector3[64], _rotation = new Vector3[64], _scale = new Vector3[64];
    Matrix4x4[] _world = new Matrix4x4[64];
    bool[] _dirty = new bool[64], _hidden = new bool[64], _locked = new bool[64];
    uint[] _version = new uint[64];
    string[] _name = new string[64];
    long[] _persistent = new long[64];

    readonly Dictionary<Type, IComponentStore> _stores = [];
    readonly List<IComponentStore> _storeList = [];

    int _iterating;
    readonly List<EntityId> _pendingDestroy = [];
    readonly List<Action> _deferred = [];

    /// <summary>Raised just before an entity is destroyed (its children first), while its components still exist.</summary>
    public event Action<EntityId>? Destroying;

    /// <summary>Live entities.</summary>
    public int Count => _count;

    /// <summary>The next persistent id <see cref="Create"/> hands out; scene files carry it, so ids stay unique within a scene.</summary>
    public long NextPersistentId = 1;

    // --- Identity ---------------------------------------------------------------------------------

    public EntityId Create(string name = "Node", EntityId parent = default, long persistentId = 0)
    {
        int i;
        if (_free.Count > 0)
            i = _free.Pop();
        else
        {
            i = _highWater++;
            if (i >= _alive.Length)
                Grow(i + 1);
        }
        if (_generation[i] == 0)
            _generation[i] = 1;
        _alive[i] = true;
        _count++;
        _parent[i] = _firstChild[i] = _lastChild[i] = _next[i] = _prev[i] = NoIndex;
        _position[i] = Vector3.Zero;
        _rotation[i] = Vector3.Zero;
        _scale[i] = Vector3.One;
        _world[i] = Matrix4x4.Identity;
        _dirty[i] = true;
        _hidden[i] = false;
        _locked[i] = false;
        _name[i] = name;
        _persistent[i] = persistentId != 0 ? persistentId : NextPersistentId++;
        if (persistentId >= NextPersistentId)
            NextPersistentId = persistentId + 1;
        var id = new EntityId(i, _generation[i]);
        Link(i, parent.IsNone ? NoIndex : Index(parent), -1);
        return id;
    }

    void Grow(int needed)
    {
        var n = Math.Max(needed, _alive.Length * 2);
        Array.Resize(ref _generation, n);
        Array.Resize(ref _alive, n);
        Array.Resize(ref _parent, n);
        Array.Resize(ref _firstChild, n);
        Array.Resize(ref _lastChild, n);
        Array.Resize(ref _next, n);
        Array.Resize(ref _prev, n);
        Array.Resize(ref _position, n);
        Array.Resize(ref _rotation, n);
        Array.Resize(ref _scale, n);
        Array.Resize(ref _world, n);
        Array.Resize(ref _dirty, n);
        Array.Resize(ref _hidden, n);
        Array.Resize(ref _locked, n);
        Array.Resize(ref _version, n);
        Array.Resize(ref _name, n);
        Array.Resize(ref _persistent, n);
    }

    public bool IsAlive(EntityId e) => e.Index >= 0 && e.Index < _highWater && _alive[e.Index] && _generation[e.Index] == e.Generation;

    /// <summary>The slot of a live entity; throws for destroyed or unknown ids, so stale handles fail loudly.</summary>
    int Index(EntityId e) =>
        IsAlive(e) ? e.Index : throw new InvalidOperationException(e.IsNone ? "no entity (EntityId.None)" : $"entity {e} has been destroyed");

    EntityId IdAt(int i) => i == NoIndex ? EntityId.None : new EntityId(i, _generation[i]);

    /// <summary>Destroys an entity and all its descendants (now, or when the current iteration ends).</summary>
    public void Destroy(EntityId e)
    {
        if (!IsAlive(e))
            return;
        if (_iterating > 0)
        {
            _pendingDestroy.Add(e);
            return;
        }
        DestroyNow(e.Index);
    }

    void DestroyNow(int i)
    {
        // Children first, so each sees an intact parent while it goes
        while (_firstChild[i] != NoIndex)
            DestroyNow(_firstChild[i]);
        Destroying?.Invoke(IdAt(i));
        var id = IdAt(i);
        foreach (var store in _storeList)
            store.Remove(id);
        Unlink(i);
        _alive[i] = false;
        _name[i] = "";
        _generation[i]++;
        if (_generation[i] == int.MaxValue)
            _generation[i] = 1; // after two billion reuses, start over (still never 0, which means none)
        _free.Push(i);
        _count--;
    }

    /// <summary>Starts a stretch in which destruction and component removal are queued; nestable.</summary>
    public void BeginIteration() => _iterating++;

    /// <summary>Ends a stretch begun by <see cref="BeginIteration"/>; the outermost end applies what was queued.</summary>
    public void EndIteration()
    {
        if (--_iterating > 0)
            return;
        while (_pendingDestroy.Count > 0 || _deferred.Count > 0)
        {
            var destroy = _pendingDestroy.ToArray();
            _pendingDestroy.Clear();
            foreach (var e in destroy)
                if (IsAlive(e))
                    DestroyNow(e.Index);
            var actions = _deferred.ToArray();
            _deferred.Clear();
            foreach (var a in actions)
                a();
        }
    }

    public bool Iterating => _iterating > 0;

    /// <summary>Runs <paramref name="action"/> now, or when the current iteration ends.</summary>
    public void Defer(Action action)
    {
        if (_iterating > 0)
            _deferred.Add(action);
        else
            action();
    }

    // --- Names and persistent ids -------------------------------------------------------------------

    public string Name(EntityId e) => _name[Index(e)];
    public void SetName(EntityId e, string name) => _name[Index(e)] = name;

    /// <summary>The entity's id in its scene file: stable across saves and loads, unique within the scene. Not the runtime id.</summary>
    public long PersistentId(EntityId e) => _persistent[Index(e)];

    public void SetPersistentId(EntityId e, long id)
    {
        _persistent[Index(e)] = id;
        if (id >= NextPersistentId)
            NextPersistentId = id + 1;
    }

    /// <summary>Hidden nodes and their descendants aren't drawn. Editor visibility and runtime visibility are the same flag.</summary>
    public bool Hidden(EntityId e) => _hidden[Index(e)];
    public void SetHidden(EntityId e, bool hidden) => _hidden[Index(e)] = hidden;

    /// <summary>Locked nodes can't be picked or moved in the editor's viewport; the game ignores it.</summary>
    public bool Locked(EntityId e) => _locked[Index(e)];
    public void SetLocked(EntityId e, bool locked) => _locked[Index(e)] = locked;

    /// <summary>Whether the entity or any ancestor is hidden.</summary>
    public bool HiddenInHierarchy(EntityId e)
    {
        for (var i = Index(e); i != NoIndex; i = _parent[i])
            if (_hidden[i])
                return true;
        return false;
    }

    // --- Hierarchy ----------------------------------------------------------------------------------

    public EntityId Parent(EntityId e) => IdAt(_parent[Index(e)]);

    public ChildList Children(EntityId e) => new(this, _firstChild[Index(e)]);

    /// <summary>Entities without a parent, in order.</summary>
    public ChildList Roots => new(this, _firstRoot);

    public int ChildCount(EntityId e)
    {
        var n = 0;
        for (var c = _firstChild[Index(e)]; c != NoIndex; c = _next[c])
            n++;
        return n;
    }

    /// <summary>Position among its siblings (or among the roots).</summary>
    public int SiblingIndex(EntityId e)
    {
        var n = 0;
        for (var c = _prev[Index(e)]; c != NoIndex; c = _prev[c])
            n++;
        return n;
    }

    /// <summary>Whether <paramref name="ancestor"/> is <paramref name="e"/> or one of its ancestors.</summary>
    public bool IsSelfOrAncestor(EntityId ancestor, EntityId e)
    {
        var a = Index(ancestor);
        for (var i = Index(e); i != NoIndex; i = _parent[i])
            if (i == a)
                return true;
        return false;
    }

    /// <summary>Whether <paramref name="child"/> can move under <paramref name="parent"/> (none: the roots) without making a cycle.</summary>
    public bool CanParent(EntityId child, EntityId parent) =>
        IsAlive(child) && (parent.IsNone || (IsAlive(parent) && !IsSelfOrAncestor(child, parent)));

    /// <summary>
    /// Moves <paramref name="child"/> under <paramref name="parent"/> (or to the roots) at sibling
    /// <paramref name="index"/> (-1: last). With <paramref name="keepWorld"/>, its local transform changes
    /// so it stays where it is in the world (exact for unskewed transforms). Throws on a cycle.
    /// </summary>
    public void SetParent(EntityId child, EntityId parent, int index = -1, bool keepWorld = false)
    {
        if (!CanParent(child, parent))
            throw new InvalidOperationException($"can't put {Describe(child)} under {Describe(parent)}: it would be its own ancestor");
        var i = Index(child);
        Matrix4x4 world = keepWorld ? WorldMatrix(child) : default;
        Unlink(i);
        Link(i, parent.IsNone ? NoIndex : Index(parent), index);
        if (keepWorld)
        {
            var parentWorld = parent.IsNone ? Matrix4x4.Identity : WorldMatrix(parent);
            Matrix4x4.Invert(parentWorld, out var inv);
            SetLocalMatrix(child, world * inv);
        }
        MarkDirty(i);
    }

    string Describe(EntityId e) => e.IsNone ? "the root" : IsAlive(e) ? $"'{_name[e.Index]}' {e}" : $"destroyed {e}";

    void Link(int i, int parent, int index)
    {
        _parent[i] = parent;
        ref var first = ref parent == NoIndex ? ref _firstRoot : ref _firstChild[parent];
        ref var last = ref parent == NoIndex ? ref _lastRoot : ref _lastChild[parent];
        var before = NoIndex; // insert before this sibling (none: at the end)
        if (index >= 0)
        {
            before = first;
            for (var n = 0; n < index && before != NoIndex; n++)
                before = _next[before];
        }
        if (before == NoIndex)
        {
            _prev[i] = last;
            _next[i] = NoIndex;
            if (last != NoIndex)
                _next[last] = i;
            else
                first = i;
            last = i;
        }
        else
        {
            _next[i] = before;
            _prev[i] = _prev[before];
            if (_prev[before] != NoIndex)
                _next[_prev[before]] = i;
            else
                first = i;
            _prev[before] = i;
        }
    }

    void Unlink(int i)
    {
        var parent = _parent[i];
        ref var first = ref parent == NoIndex ? ref _firstRoot : ref _firstChild[parent];
        ref var last = ref parent == NoIndex ? ref _lastRoot : ref _lastChild[parent];
        if (_prev[i] != NoIndex)
            _next[_prev[i]] = _next[i];
        else
            first = _next[i];
        if (_next[i] != NoIndex)
            _prev[_next[i]] = _prev[i];
        else
            last = _prev[i];
        _parent[i] = _next[i] = _prev[i] = NoIndex;
    }

    /// <summary>A node's children in order, without allocating. Don't add or remove its children while walking it (destroy is deferred while iterating).</summary>
    public readonly struct ChildList(World world, int first)
    {
        public Enumerator GetEnumerator() => new(world, first);

        /// <summary>A copy, for when the children may change while going through them.</summary>
        public List<EntityId> ToList()
        {
            var list = new List<EntityId>();
            foreach (var e in this)
                list.Add(e);
            return list;
        }

        public struct Enumerator(World world, int first)
        {
            int _next = first;
            public EntityId Current { get; private set; }

            public bool MoveNext()
            {
                if (_next == NoIndex)
                    return false;
                Current = world.IdAt(_next);
                _next = world._next[_next];
                return true;
            }
        }
    }

    /// <summary>The entity and everything under it, parents before children.</summary>
    public List<EntityId> Subtree(EntityId e, List<EntityId>? into = null)
    {
        into ??= [];
        into.Add(e);
        foreach (var c in Children(e))
            Subtree(c, into);
        return into;
    }

    /// <summary>Every live entity, parents before children, roots in order.</summary>
    public List<EntityId> All()
    {
        var list = new List<EntityId>(_count);
        foreach (var r in Roots)
            Subtree(r, list);
        return list;
    }

    // --- Transforms ---------------------------------------------------------------------------------

    public Vector3 Position(EntityId e) => _position[Index(e)];
    public Vector3 Rotation(EntityId e) => _rotation[Index(e)];
    public Vector3 Scale(EntityId e) => _scale[Index(e)];

    public void SetPosition(EntityId e, Vector3 v) => SetLocal(Index(e), ref _position[e.Index], v);
    /// <summary>Local rotation in degrees, applied X, then Y, then Z.</summary>
    public void SetRotation(EntityId e, Vector3 degrees) => SetLocal(Index(e), ref _rotation[e.Index], degrees);
    public void SetScale(EntityId e, Vector3 v) => SetLocal(Index(e), ref _scale[e.Index], v);

    void SetLocal(int i, ref Vector3 field, Vector3 value)
    {
        if (field == value)
            return;
        field = value;
        MarkDirty(i);
    }

    public static Matrix4x4 Compose(Vector3 position, Vector3 rotationDegrees, Vector3 scale) =>
        Matrix4x4.CreateScale(scale)
        * Matrix4x4.CreateRotationX(float.DegreesToRadians(rotationDegrees.X))
        * Matrix4x4.CreateRotationY(float.DegreesToRadians(rotationDegrees.Y))
        * Matrix4x4.CreateRotationZ(float.DegreesToRadians(rotationDegrees.Z))
        * Matrix4x4.CreateTranslation(position);

    public Matrix4x4 LocalMatrix(EntityId e)
    {
        var i = Index(e);
        return Compose(_position[i], _rotation[i], _scale[i]);
    }

    /// <summary>Sets the local transform from a matrix (scale, rotation, translation; skew is lost).</summary>
    public void SetLocalMatrix(EntityId e, Matrix4x4 m)
    {
        if (!Matrix4x4.Decompose(m, out var scale, out var q, out var translation))
            return;
        var i = Index(e);
        _position[i] = translation;
        _scale[i] = scale;
        _rotation[i] = EulerDegrees(q);
        MarkDirty(i);
    }

    /// <summary>Euler angles (degrees, X then Y then Z, as <see cref="Compose"/> applies them) of a rotation.</summary>
    public static Vector3 EulerDegrees(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        // Row-vector M = Rx * Ry * Rz: M13 = -sin(y)
        var y = MathF.Asin(Math.Clamp(-m.M13, -1, 1));
        float x, z;
        if (MathF.Abs(m.M13) < 0.9999f)
        {
            x = MathF.Atan2(m.M23, m.M33);
            z = MathF.Atan2(m.M12, m.M11);
        }
        else
        {
            // Gimbal lock: fold Z into X
            x = MathF.Atan2(-m.M32, m.M22);
            z = 0;
        }
        return new Vector3(float.RadiansToDegrees(x), float.RadiansToDegrees(y), float.RadiansToDegrees(z));
    }

    void MarkDirty(int i)
    {
        // A dirty node's descendants are always dirty, so stop at any that already are
        if (_dirty[i])
            return;
        _dirty[i] = true;
        for (var c = _firstChild[i]; c != NoIndex; c = _next[c])
            MarkDirty(c);
    }

    /// <summary>The entity's world matrix, recomputed only if it or an ancestor changed since it was last asked for.</summary>
    public Matrix4x4 WorldMatrix(EntityId e)
    {
        var i = Index(e);
        if (_dirty[i])
            UpdateWorld(i);
        return _world[i];
    }

    public Vector3 WorldPosition(EntityId e) => WorldMatrix(e).Translation;

    /// <summary>Increases whenever the entity's world matrix is recomputed: compare to see if it moved.</summary>
    public uint TransformVersion(EntityId e)
    {
        WorldMatrix(e);
        return _version[e.Index];
    }

    void UpdateWorld(int i)
    {
        var parent = _parent[i];
        if (parent != NoIndex && _dirty[parent])
            UpdateWorld(parent);
        var local = Compose(_position[i], _rotation[i], _scale[i]);
        _world[i] = parent == NoIndex ? local : local * _world[parent];
        _dirty[i] = false;
        _version[i]++;
    }

    /// <summary>How many world matrices are waiting to be recomputed (for tests and profiling).</summary>
    public int DirtyCount
    {
        get
        {
            var n = 0;
            for (var i = 0; i < _highWater; i++)
                if (_alive[i] && _dirty[i])
                    n++;
            return n;
        }
    }

    // --- Components ---------------------------------------------------------------------------------

    /// <summary>The store for one component type, created on first use.</summary>
    public ComponentStore<T> Store<T>() where T : struct
    {
        if (_stores.TryGetValue(typeof(T), out var s))
            return (ComponentStore<T>)s;
        var store = new ComponentStore<T>();
        _stores[typeof(T)] = store;
        _storeList.Add(store);
        return store;
    }

    public ref T Add<T>(EntityId e, in T value) where T : struct
    {
        Index(e);
        return ref Store<T>().Add(e, value);
    }

    public ref T Get<T>(EntityId e) where T : struct
    {
        Index(e);
        return ref Store<T>().Get(e);
    }

    public bool Has<T>(EntityId e) where T : struct => IsAlive(e) && Store<T>().Has(e);

    /// <summary>Removes a component (now, or when the current iteration ends).</summary>
    public void Remove<T>(EntityId e) where T : struct
    {
        if (!IsAlive(e))
            return;
        if (_iterating > 0)
            _deferred.Add(() => Store<T>().Remove(e));
        else
            Store<T>().Remove(e);
    }

    /// <summary>Destroys every entity (for reloading a scene into the same world).</summary>
    public void Clear()
    {
        while (_firstRoot != NoIndex)
            DestroyNow(_firstRoot);
        NextPersistentId = 1;
    }
}
