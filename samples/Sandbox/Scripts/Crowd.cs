using System.Numerics;
using Trinity;
using Trinity.Scenes;

/// <summary>Fills a square with animated copies of its node's model, as its children: a stress test for instancing and shadows.</summary>
public sealed class Crowd : Script
{
    public int Count = 2000;
    public float Spacing = 1.5f;

    public override void Ready()
    {
        var model = Node.Get<MeshRenderer>().Mesh;
        var side = (int)MathF.Ceiling(MathF.Sqrt(Count));
        var random = new Random(1);
        string[] clips = ["idle", "walk", "attack"];
        for (var i = 0; i < Count; i++)
        {
            var member = Node.AddChild($"{Node.Name} {i}");
            member.Position = new Vector3((i % side - side / 2f) * Spacing, 0, (i / side - side / 2f) * Spacing);
            member.Rotation = new Vector3(0, random.NextSingle() * 360, 0);
            member.Add(new MeshRenderer(model));
            member.Add(new Animator(clips[random.Next(clips.Length)]) { Time = random.NextSingle() * 2 });
        }
        Node.Remove<MeshRenderer>(); // the crowd replaces this node's own model
    }
}
