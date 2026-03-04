namespace UnsafeApiBrowser;

public static class MarkerService
{
    const string Marker = " /*[RequiresUnsafe]*/";

    /// <summary>
    /// Builds a flat lookup of all nodes by Id for quick access.
    /// </summary>
    public static Dictionary<string, ApiNode> BuildIndex(List<ApiNode> roots)
    {
        var index = new Dictionary<string, ApiNode>(StringComparer.Ordinal);
        void Walk(ApiNode node)
        {
            index[node.Id] = node;
            if (node.Children is not null)
                foreach (var child in node.Children)
                    Walk(child);
        }
        foreach (var root in roots)
            Walk(root);
        return index;
    }

    /// <summary>
    /// Toggles the MARKER comment on the source line for the given node.
    /// Returns true if the operation was successful.
    /// </summary>
    public static bool ToggleMarker(ApiNode node, bool marked)
    {
        if (node.SourceFile is null || node.SourceLine <= 0)
            return false;

        var lines = File.ReadAllLines(node.SourceFile);
        var idx = node.SourceLine - 1;
        if (idx < 0 || idx >= lines.Length)
            return false;

        var line = lines[idx];

        if (marked)
        {
            if (!line.Contains("/*[RequiresUnsafe]*/", StringComparison.Ordinal))
            {
                // Add marker before trailing newline/whitespace
                lines[idx] = line.TrimEnd() + Marker;
            }
        }
        else
        {
            if (line.Contains("/*[RequiresUnsafe]*/", StringComparison.Ordinal))
            {
                lines[idx] = line.Replace(Marker, "").Replace("/*[RequiresUnsafe]*/", "");
            }
        }

        File.WriteAllLines(node.SourceFile, lines);
        node.IsMarked = marked;
        return true;
    }
}
