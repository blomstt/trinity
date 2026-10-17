using System.Numerics;
using Trinity.Scenes;
using Xunit;

namespace Trinity.Tests;

public class EntityTests
{
    [Fact]
    public void Created_entities_are_alive_until_destroyed()
    {
        var w = new World();
        var a = w.Create("a");
        var b = w.Create("b");
        Assert.True(w.IsAlive(a) && w.IsAlive(b));
        Assert.NotEqual(a, b);
        Assert.Equal(2, w.Count);
        w.Destroy(a);
        Assert.False(w.IsAlive(a));
        Assert.True(w.IsAlive(b));
        Assert.Equal(1, w.Count);
        Assert.False(w.IsAlive(EntityId.None));
    }

    [Fact]
    public void A_stale_handle_never_reaches_the_entity_that_reuses_its_slot()
    {
        var w = new World();
        var old = w.Create("old");
        w.Destroy(old);
        var reused = w.Create("new");
        Assert.Equal(old.Index, reused.Index); // the slot is reused...
        Assert.NotEqual(old.Generation, reused.Generation); // ...with a new generation
        Assert.False(w.IsAlive(old));
        var e = Assert.Throws<InvalidOperationException>(() => w.Name(old));
        Assert.Contains("destroyed", e.Message);
        Assert.Equal("new", w.Name(reused));
    }

    [Fact]
    public void Components_are_added_queried_and_removed()
    {
        var w = new World();
        var a = w.Create();
        var b = w.Create();
        w.Add(a, new MeshRenderer("a.glb"));
        w.Add(b, new MeshRenderer("b.glb"));
        w.Add(b, new Animator("walk"));
        Assert.True(w.Has<MeshRenderer>(a));
        Assert.False(w.Has<Animator>(a));
        Assert.Throws<InvalidOperationException>(() => w.Add(a, new MeshRenderer("again.glb"))); // one per entity

        // Edited in place through the ref
        w.Get<Animator>(b).Speed = 2;
        Assert.Equal(2, w.Get<Animator>(b).Speed);

        var store = w.Store<MeshRenderer>();
        Assert.Equal(2, store.Count);
        Assert.Equal(["a.glb", "b.glb"], store.Components.ToArray().Select(c => c.Mesh));

        w.Remove<MeshRenderer>(a);
        Assert.False(w.Has<MeshRenderer>(a));
        Assert.Equal("b.glb", store.Components[0].Mesh); // the last one moved into the gap
        Assert.Equal(b, store.Entities[0]);
    }

    [Fact]
    public void Destroying_an_entity_removes_its_components_and_stale_lookups_fail()
    {
        var w = new World();
        var a = w.Create();
        w.Add(a, new MeshRenderer("a.glb"));
        w.Destroy(a);
        Assert.Equal(0, w.Store<MeshRenderer>().Count);
        Assert.False(w.Has<MeshRenderer>(a));
        Assert.Throws<InvalidOperationException>(() => w.Get<MeshRenderer>(a));
        var reused = w.Create();
        Assert.False(w.Has<MeshRenderer>(reused));
    }

    [Fact]
    public void Destruction_during_iteration_waits_until_it_ends()
    {
        var w = new World();
        var ids = Enumerable.Range(0, 5).Select(i => w.Create($"n{i}")).ToList();
        foreach (var id in ids)
            w.Add(id, new MeshRenderer($"{id.Index}.glb"));

        w.BeginIteration();
        var seen = 0;
        var store = w.Store<MeshRenderer>();
        for (var i = 0; i < store.Count; i++)
        {
            seen++;
            w.Destroy(store.Entities[i]); // would scramble the store if it happened now
            w.Remove<MeshRenderer>(store.Entities[i]);
        }
        Assert.Equal(5, seen);
        Assert.Equal(5, w.Count);
        w.EndIteration();
        Assert.Equal(0, w.Count);
        Assert.Equal(0, store.Count);
    }
}

public class HierarchyTests
{
    [Fact]
    public void Children_keep_their_order_and_can_be_inserted_anywhere()
    {
        var w = new World();
        var p = w.Create("p");
        var a = w.Create("a", p);
        var c = w.Create("c", p);
        var b = w.Create("b");
        w.SetParent(b, p, index: 1);
        Assert.Equal(["a", "b", "c"], Names(w, p));
        Assert.Equal(p, w.Parent(b));
        Assert.Equal(1, w.SiblingIndex(b));
        w.SetParent(c, p, index: 0);
        Assert.Equal(["c", "a", "b"], Names(w, p));
        Assert.Equal(3, w.ChildCount(p));
        w.SetParent(a, EntityId.None);
        Assert.Equal(["c", "b"], Names(w, p));
        Assert.Contains(a, Roots(w));
    }

    [Fact]
    public void Reparenting_that_would_make_a_cycle_is_refused()
    {
        var w = new World();
        var a = w.Create("a");
        var b = w.Create("b", a);
        var c = w.Create("c", b);
        Assert.False(w.CanParent(a, c));
        Assert.False(w.CanParent(a, a));
        Assert.Throws<InvalidOperationException>(() => w.SetParent(a, c));
        Assert.Equal(EntityId.None, w.Parent(a)); // unchanged
        Assert.True(w.CanParent(c, a));
    }

    [Fact]
    public void Destroying_a_parent_destroys_its_descendants_children_first()
    {
        var w = new World();
        var a = w.Create("a");
        var b = w.Create("b", a);
        var c = w.Create("c", b);
        var keep = w.Create("keep");
        var order = new List<string>();
        w.Destroying += e => order.Add(w.Name(e));
        w.Destroy(a);
        Assert.Equal(["c", "b", "a"], order);
        Assert.False(w.IsAlive(b) || w.IsAlive(c));
        Assert.True(w.IsAlive(keep));
        Assert.Equal([keep], Roots(w));
    }

    [Fact]
    public void World_transforms_combine_the_ancestors()
    {
        var w = new World();
        var parent = w.Create("parent");
        w.SetPosition(parent, new Vector3(10, 0, 0));
        w.SetRotation(parent, new Vector3(0, 90, 0));
        w.SetScale(parent, new Vector3(2));
        var child = w.Create("child", parent);
        w.SetPosition(child, new Vector3(1, 0, 0));

        // Scaled by 2, turned 90 degrees about Y (+X becomes -Z), then moved by +10 X
        AssertNear(new Vector3(10, 0, -2), w.WorldPosition(child));

        // Moving the parent moves the child
        w.SetPosition(parent, new Vector3(0, 5, 0));
        AssertNear(new Vector3(0, 5, -2), w.WorldPosition(child));
    }

    [Fact]
    public void World_matrices_are_only_recomputed_when_something_changed()
    {
        var w = new World();
        var a = w.Create("a");
        var b = w.Create("b", a);
        var c = w.Create("c", b);
        var other = w.Create("other");
        w.WorldMatrix(c);
        w.WorldMatrix(other);
        Assert.Equal(0, w.DirtyCount);
        var before = (w.TransformVersion(a), w.TransformVersion(c), w.TransformVersion(other));

        // Asking again recomputes nothing
        w.WorldMatrix(c);
        Assert.Equal(before, (w.TransformVersion(a), w.TransformVersion(c), w.TransformVersion(other)));

        // Changing b dirties b and c, not a or the unrelated node
        w.SetPosition(b, Vector3.One);
        Assert.Equal(2, w.DirtyCount);
        Assert.Equal(before.Item1, w.TransformVersion(a));
        Assert.Equal(before.Item3, w.TransformVersion(other));
        Assert.NotEqual(before.Item2, w.TransformVersion(c));

        // Setting the same value again changes nothing
        w.WorldMatrix(c);
        w.SetPosition(b, Vector3.One);
        Assert.Equal(0, w.DirtyCount);
    }

    [Fact]
    public void Reparenting_can_keep_the_world_position()
    {
        var w = new World();
        var a = w.Create("a");
        w.SetPosition(a, new Vector3(3, 0, 0));
        w.SetRotation(a, new Vector3(0, 45, 0));
        var b = w.Create("b");
        w.SetPosition(b, new Vector3(1, 2, 3));
        w.SetParent(b, a, keepWorld: true);
        AssertNear(new Vector3(1, 2, 3), w.WorldPosition(b));
        w.SetParent(b, EntityId.None, keepWorld: false);
        AssertNear(w.Position(b), w.WorldPosition(b));
    }

    [Fact]
    public void Deep_hierarchies_resolve_correctly()
    {
        var w = new World();
        var parent = EntityId.None;
        EntityId last = default;
        for (var i = 0; i < 2000; i++)
        {
            last = w.Create($"n{i}", parent);
            w.SetPosition(last, new Vector3(1, 0, 0));
            parent = last;
        }
        AssertNear(new Vector3(2000, 0, 0), w.WorldPosition(last));
        w.SetPosition(w.Roots.GetEnumerator() is var e && e.MoveNext() ? e.Current : default, new Vector3(5, 0, 0));
        AssertNear(new Vector3(2004, 0, 0), w.WorldPosition(last));
        w.Destroy(w.Roots.GetEnumerator() is var r && r.MoveNext() ? r.Current : default);
        Assert.Equal(0, w.Count);
    }

    [Fact]
    public void Euler_angles_survive_a_round_trip_through_a_matrix()
    {
        var w = new World();
        var e = w.Create();
        w.SetRotation(e, new Vector3(30, -40, 70));
        var m = w.LocalMatrix(e);
        w.SetLocalMatrix(e, m);
        AssertNear(new Vector3(30, -40, 70), w.Rotation(e), 1e-3f);
    }

    static List<string> Names(World w, EntityId parent)
    {
        var names = new List<string>();
        foreach (var c in w.Children(parent))
            names.Add(w.Name(c));
        return names;
    }

    static List<EntityId> Roots(World w)
    {
        var list = new List<EntityId>();
        foreach (var r in w.Roots)
            list.Add(r);
        return list;
    }

    internal static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");
}
