using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// Orders shapes so every shape comes after the shapes it dependsOn.
// Throws on unknown dependency or cycle.
public static class TopologicalSorter
{
    public static IReadOnlyList<Shape> Order(IReadOnlyList<Shape> shapes)
    {
        var byName = shapes.ToDictionary(s => s.Metadata.Name, StringComparer.Ordinal);
        var ordered = new List<Shape>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=unseen,1=visiting,2=done

        void Visit(Shape s)
        {
            var name = s.Metadata.Name;
            if (state.TryGetValue(name, out var st))
            {
                if (st == 1) throw new InvalidOperationException($"Dependency cycle involving '{name}'.");
                if (st == 2) return;
            }
            state[name] = 1;
            foreach (var dep in s.Spec.DependsOn)
            {
                if (!byName.TryGetValue(dep, out var depShape))
                    throw new InvalidOperationException($"'{name}' dependsOn '{dep}', which is not a member of this stack.");
                Visit(depShape);
            }
            state[name] = 2;
            ordered.Add(s);
        }

        foreach (var s in shapes) Visit(s);
        return ordered;
    }
}
