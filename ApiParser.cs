using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnsafeApiBrowser;

public static class ApiParser
{
    const string Marker = "/*[RequiresUnsafe]*/";

    public static List<ApiNode> Parse(string runtimeRepoPath)
    {
        var libPath = Path.Combine(runtimeRepoPath, "src", "libraries");
        if (!Directory.Exists(libPath))
            throw new DirectoryNotFoundException($"Libraries path not found: {libPath}");

        var refFiles = Directory.EnumerateFiles(libPath, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var dir = Path.GetDirectoryName(f)!;
                return Path.GetFileName(dir).Equals("ref", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        Console.WriteLine($"Found {refFiles.Count} ref files to parse...");

        // Parse all files in parallel — each produces its own namespace dict
        var perFileResults = new Dictionary<string, ApiNode>[refFiles.Count];
        Parallel.For(0, refFiles.Count, i =>
        {
            var file = refFiles[i];
            var source = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(source, path: file,
                options: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
            var root = tree.GetCompilationUnitRoot();
            var lines = source.Split('\n');

            var localNamespaces = new Dictionary<string, ApiNode>(StringComparer.Ordinal);
            foreach (var member in root.Members)
            {
                if (member is BaseNamespaceDeclarationSyntax ns)
                    ProcessNamespace(ns, file, lines, localNamespaces);
            }
            perFileResults[i] = localNamespaces;
        });

        // Merge results sequentially
        var rootNamespaces = new Dictionary<string, ApiNode>(StringComparer.Ordinal);
        foreach (var localNs in perFileResults)
            MergeNamespaces(rootNamespaces, localNs);

        var result = NestNamespaces(rootNamespaces.Values.ToList());
        foreach (var ns in result)
            ns.SortChildren();
        return result;
    }

    static void MergeNamespaces(Dictionary<string, ApiNode> target, Dictionary<string, ApiNode> source)
    {
        foreach (var (name, srcNode) in source)
        {
            if (!target.TryGetValue(name, out var tgtNode))
            {
                target[name] = srcNode;
                continue;
            }
            // Merge children
            if (srcNode.Children is null) continue;
            tgtNode.Children ??= [];
            foreach (var child in srcNode.Children)
            {
                if (child.Kind == ApiNodeKind.Namespace)
                {
                    var existingNs = tgtNode.Children.FirstOrDefault(
                        c => c.Kind == ApiNodeKind.Namespace && c.Name == child.Name);
                    if (existingNs is not null)
                    {
                        var childDict = new Dictionary<string, ApiNode>(StringComparer.Ordinal) { [child.Name] = child };
                        var tgtDict = new Dictionary<string, ApiNode>(StringComparer.Ordinal) { [existingNs.Name] = existingNs };
                        MergeNamespaces(tgtDict, childDict);
                    }
                    else
                    {
                        tgtNode.Children.Add(child);
                    }
                }
                else
                {
                    // Types: merge partial types
                    var existing = tgtNode.Children.FirstOrDefault(c => c.Id == child.Id);
                    if (existing is not null)
                    {
                        if (child.IsMarked) existing.IsMarked = true;
                        if (child.IsUnsafe) existing.IsUnsafe = true;
                        if (child.Children is not null)
                        {
                            existing.Children ??= [];
                            foreach (var member in child.Children)
                            {
                                if (!existing.Children.Any(m => m.Id == member.Id))
                                    existing.Children.Add(member);
                            }
                        }
                    }
                    else
                    {
                        tgtNode.Children.Add(child);
                    }
                }
            }
        }
    }

    static void ProcessNamespace(
        BaseNamespaceDeclarationSyntax ns,
        string file,
        string[] lines,
        Dictionary<string, ApiNode> siblings)
    {
        var nsName = ns.Name.ToString();

        if (!siblings.TryGetValue(nsName, out var nsNode))
        {
            nsNode = new ApiNode
            {
                Id = nsName,
                Name = nsName,
                Kind = ApiNodeKind.Namespace,
                Children = [],
            };
            siblings[nsName] = nsNode;
        }

        // Child namespaces
        var childNamespaces = new Dictionary<string, ApiNode>(StringComparer.Ordinal);
        foreach (var existing in nsNode.Children!.Where(c => c.Kind == ApiNodeKind.Namespace))
            childNamespaces[existing.Name] = existing;

        foreach (var member in ns.Members)
        {
            if (member is BaseNamespaceDeclarationSyntax childNs)
            {
                ProcessNamespace(childNs, file, lines, childNamespaces);
            }
            else if (member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            {
                ProcessType(member, file, lines, nsNode);
            }
        }

        // Add any new child namespaces
        foreach (var (name, child) in childNamespaces)
        {
            if (!nsNode.Children!.Any(c => c.Kind == ApiNodeKind.Namespace && c.Name == name))
                nsNode.Children!.Add(child);
        }
    }

    static void ProcessType(
        MemberDeclarationSyntax member,
        string file,
        string[] lines,
        ApiNode parent)
    {
        var (name, kind) = member switch
        {
            ClassDeclarationSyntax c => (GetTypeName(c), c.Keyword.Text == "record" ? ApiNodeKind.Record : ApiNodeKind.Class),
            RecordDeclarationSyntax r => (GetTypeName(r), r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? ApiNodeKind.RecordStruct : ApiNodeKind.Record),
            StructDeclarationSyntax s => (GetTypeName(s), ApiNodeKind.Struct),
            InterfaceDeclarationSyntax i => (GetTypeName(i), ApiNodeKind.Interface),
            EnumDeclarationSyntax e => (e.Identifier.Text, ApiNodeKind.Enum),
            DelegateDeclarationSyntax d => (GetDelegateSignature(d), ApiNodeKind.Delegate),
            _ => (null, ApiNodeKind.Class),
        };

        if (name is null) return;

        var id = $"{parent.Id}.{name}";
        var line = GetDeclarationLine(member);
        var isMarked = IsLineMarked(lines, line);
        var isUnsafe = HasUnsafeModifier(member);

        // Find existing type node (partial types across files)
        var existing = parent.Children!.FirstOrDefault(c => c.Id == id);
        if (existing is not null)
        {
            if (isMarked) existing.IsMarked = true;
            if (isUnsafe) existing.IsUnsafe = true;
            ProcessTypeMembers(member, file, lines, existing);
            return;
        }

        var node = new ApiNode
        {
            Id = id,
            Name = name,
            Kind = kind,
            IsMarked = isMarked,
            IsUnsafe = isUnsafe,
            HasPointers = ContainsPointerType(member),
            SourceFile = file,
            SourceLine = line + 1,
            Children = [],
        };

        ProcessTypeMembers(member, file, lines, node);
        parent.Children!.Add(node);
    }

    static void ProcessTypeMembers(
        MemberDeclarationSyntax typeSyntax,
        string file,
        string[] lines,
        ApiNode typeNode)
    {
        // If the type itself is unsafe, all members inherit it
        bool parentUnsafe = typeNode.IsUnsafe;

        IEnumerable<MemberDeclarationSyntax> members = typeSyntax switch
        {
            TypeDeclarationSyntax t => t.Members,
            EnumDeclarationSyntax e => e.Members,
            _ => [],
        };

        foreach (var m in members)
        {
            if (m is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            {
                ProcessType(m, file, lines, typeNode);
                // Propagate parent unsafe to nested types after they're added
                if (parentUnsafe)
                {
                    var nestedName = m switch
                    {
                        TypeDeclarationSyntax t => GetTypeName(t),
                        EnumDeclarationSyntax e => e.Identifier.Text,
                        DelegateDeclarationSyntax d => GetDelegateSignature(d),
                        _ => null,
                    };
                    if (nestedName is not null)
                    {
                        var nestedId = $"{typeNode.Id}.{nestedName}";
                        var nested = typeNode.Children!.FirstOrDefault(c => c.Id == nestedId);
                        if (nested is not null)
                            PropagateUnsafe(nested);
                    }
                }
                continue;
            }

            var (mName, mKind) = GetMemberInfo(m);
            if (mName is null) continue;

            var mId = $"{typeNode.Id}.{mName}";
            var mLine = GetDeclarationLine(m);
            var mMarked = IsLineMarked(lines, mLine);
            var mUnsafe = parentUnsafe || HasUnsafeModifier(m);

            // Deduplicate (same member from partial type in another file)
            if (typeNode.Children!.Any(c => c.Id == mId)) continue;

            typeNode.Children!.Add(new ApiNode
            {
                Id = mId,
                Name = mName,
                Sig = GetSignature(m),
                Kind = mKind,
                IsMarked = mMarked,
                IsUnsafe = mUnsafe,
                HasPointers = ContainsPointerType(m),
                SourceFile = file,
                SourceLine = mLine + 1,
            });
        }
    }

    static (string? Name, ApiNodeKind Kind) GetMemberInfo(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax method =>
            (GetMethodSignature(method), ApiNodeKind.Method),
        PropertyDeclarationSyntax prop =>
            (prop.Identifier.Text, ApiNodeKind.Property),
        FieldDeclarationSyntax field =>
            (field.Declaration.Variables.First().Identifier.Text, ApiNodeKind.Field),
        EventDeclarationSyntax evt =>
            (evt.Identifier.Text, ApiNodeKind.Event),
        EventFieldDeclarationSyntax evtField =>
            (evtField.Declaration.Variables.First().Identifier.Text, ApiNodeKind.Event),
        ConstructorDeclarationSyntax ctor =>
            (GetConstructorSignature(ctor), ApiNodeKind.Constructor),
        OperatorDeclarationSyntax op =>
            ($"operator {op.OperatorToken.Text}({GetParamList(op.ParameterList)})", ApiNodeKind.Operator),
        ConversionOperatorDeclarationSyntax conv =>
            ($"{conv.ImplicitOrExplicitKeyword.Text} operator {conv.Type}({GetParamList(conv.ParameterList)})", ApiNodeKind.Operator),
        IndexerDeclarationSyntax idx =>
            ($"this[{GetParamList(idx.ParameterList)}]", ApiNodeKind.Indexer),
        EnumMemberDeclarationSyntax em =>
            (em.Identifier.Text, ApiNodeKind.EnumMember),
        _ => (null, ApiNodeKind.Field),
    };

    static string GetTypeName(TypeDeclarationSyntax type)
    {
        var name = type.Identifier.Text;
        if (type.TypeParameterList is { } tpl)
            name += $"`{tpl.Parameters.Count}";
        return name;
    }

    static string GetDelegateSignature(DelegateDeclarationSyntax d)
    {
        var name = d.Identifier.Text;
        if (d.TypeParameterList is { } tpl)
            name += $"`{tpl.Parameters.Count}";
        return name;
    }

    static string GetMethodSignature(MethodDeclarationSyntax method)
    {
        var name = method.Identifier.Text;
        if (method.TypeParameterList is { } tpl)
            name += $"`{tpl.Parameters.Count}";
        name += $"({GetParamList(method.ParameterList)})";
        return name;
    }

    static string GetConstructorSignature(ConstructorDeclarationSyntax ctor)
    {
        return $".ctor({GetParamList(ctor.ParameterList)})";
    }

    static string GetParamList(BaseParameterListSyntax? paramList)
    {
        if (paramList is null) return "";
        return string.Join(", ", paramList.Parameters.Select(p =>
        {
            var type = p.Type?.ToString() ?? "?";
            return type;
        }));
    }

    static bool HasUnsafeModifier(MemberDeclarationSyntax member)
    {
        return member.Modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword));
    }

    static void PropagateUnsafe(ApiNode node)
    {
        node.IsUnsafe = true;
        if (node.Children is null) return;
        foreach (var child in node.Children)
            PropagateUnsafe(child);
    }

    static bool ContainsPointerType(MemberDeclarationSyntax member)
    {
        // Check return type, parameter types, field type, property type for pointer/function pointer syntax
        foreach (var descendant in member.DescendantNodes())
        {
            if (descendant is PointerTypeSyntax or FunctionPointerTypeSyntax)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Extracts the declaration signature without 'public', attributes, or body.
    /// </summary>
    static string GetSignature(MemberDeclarationSyntax member)
    {
        // Build modifiers string, skipping 'public'
        var mods = string.Join(" ", member.Modifiers
            .Where(m => !m.IsKind(SyntaxKind.PublicKeyword))
            .Select(m => m.Text));

        var sig = member switch
        {
            TypeDeclarationSyntax t => BuildTypeSig(mods, t),
            EnumDeclarationSyntax e => $"{PrependMods(mods)}enum {e.Identifier.Text}",
            DelegateDeclarationSyntax d => $"{PrependMods(mods)}delegate {d.ReturnType} {d.Identifier}{d.TypeParameterList}{d.ParameterList}",
            MethodDeclarationSyntax m => $"{PrependMods(mods)}{m.ReturnType} {m.Identifier}{m.TypeParameterList}({GetParamList(m.ParameterList)})",
            PropertyDeclarationSyntax p => $"{PrependMods(mods)}{p.Type} {p.Identifier}",
            FieldDeclarationSyntax f => $"{PrependMods(mods)}{f.Declaration.Type} {f.Declaration.Variables.First().Identifier}",
            EventDeclarationSyntax ev => $"{PrependMods(mods)}event {ev.Type} {ev.Identifier}",
            EventFieldDeclarationSyntax ef => $"{PrependMods(mods)}event {ef.Declaration.Type} {ef.Declaration.Variables.First().Identifier}",
            ConstructorDeclarationSyntax c => $"{PrependMods(mods)}{c.Identifier}({GetParamList(c.ParameterList)})",
            OperatorDeclarationSyntax o => $"{PrependMods(mods)}{o.ReturnType} operator {o.OperatorToken.Text}({GetParamList(o.ParameterList)})",
            ConversionOperatorDeclarationSyntax cv => $"{PrependMods(mods)}{cv.ImplicitOrExplicitKeyword.Text} operator {cv.Type}({GetParamList(cv.ParameterList)})",
            IndexerDeclarationSyntax ix => $"{PrependMods(mods)}{ix.Type} this[{GetParamList(ix.ParameterList)}]",
            EnumMemberDeclarationSyntax em => em.Identifier.Text + (em.EqualsValue is not null ? $" = {em.EqualsValue.Value}" : ""),
            _ => member.ToString().Split('\n')[0].Trim(),
        };

        return sig;
    }

    static string BuildTypeSig(string mods, TypeDeclarationSyntax t)
    {
        var keyword = t.Keyword.Text;
        if (t is RecordDeclarationSyntax r && !r.ClassOrStructKeyword.IsKind(SyntaxKind.None))
            keyword = $"record {r.ClassOrStructKeyword.Text}";

        var name = $"{t.Identifier}{t.TypeParameterList}";

        var baseList = t.BaseList is not null
            ? $" : {string.Join(", ", t.BaseList.Types)}"
            : "";

        return $"{PrependMods(mods)}{keyword} {name}{baseList}";
    }

    static string PrependMods(string mods) => mods.Length > 0 ? mods + " " : "";

    /// <summary>
    /// Gets the line number of the actual declaration keyword/identifier, skipping attributes.
    /// </summary>
    static int GetDeclarationLine(MemberDeclarationSyntax member)
    {
        // Use the first token after the attribute lists
        SyntaxToken token = member switch
        {
            TypeDeclarationSyntax type => type.Keyword,
            EnumDeclarationSyntax enumDecl => enumDecl.EnumKeyword,
            DelegateDeclarationSyntax del => del.DelegateKeyword,
            MethodDeclarationSyntax method => method.ReturnType.GetFirstToken(),
            PropertyDeclarationSyntax prop => prop.Type.GetFirstToken(),
            FieldDeclarationSyntax field => field.Declaration.Type.GetFirstToken(),
            EventDeclarationSyntax evt => evt.EventKeyword,
            EventFieldDeclarationSyntax evtField => evtField.EventKeyword,
            ConstructorDeclarationSyntax ctor => ctor.Identifier,
            OperatorDeclarationSyntax op => op.ReturnType.GetFirstToken(),
            ConversionOperatorDeclarationSyntax conv => conv.ImplicitOrExplicitKeyword,
            IndexerDeclarationSyntax idx => idx.Type.GetFirstToken(),
            EnumMemberDeclarationSyntax em => em.Identifier,
            _ => member.GetFirstToken(),
        };

        // Walk backward to the first modifier if any (public, static, etc.)
        if (member.Modifiers.Count > 0)
            token = member.Modifiers.First();

        return token.GetLocation().GetLineSpan().StartLinePosition.Line;
    }

    static bool IsLineMarked(string[] lines, int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= lines.Length) return false;
        return lines[lineIndex].Contains(Marker, StringComparison.Ordinal);
    }

    /// <summary>
    /// Takes a flat list of namespaces (e.g. "System.Collections.Generic") and nests them
    /// into a hierarchy (System → Collections → Generic).
    /// </summary>
    static List<ApiNode> NestNamespaces(List<ApiNode> flatNamespaces)
    {
        // rootMap: short name → ApiNode (namespace)
        var rootMap = new Dictionary<string, ApiNode>(StringComparer.Ordinal);

        foreach (var ns in flatNamespaces)
        {
            var parts = ns.Name.Split('.');
            InsertNamespace(rootMap, parts, 0, ns);
        }

        var result = rootMap.Values.ToList();
        return result;
    }

    static void InsertNamespace(
        Dictionary<string, ApiNode> siblingMap,
        string[] parts,
        int depth,
        ApiNode leafNs)
    {
        var part = parts[depth];
        var fullName = string.Join(".", parts[..(depth + 1)]);
        var isLeaf = depth == parts.Length - 1;

        if (!siblingMap.TryGetValue(part, out var node))
        {
            if (isLeaf)
            {
                // Use the real namespace node, just fix the display name
                leafNs.Name = part;
                siblingMap[part] = leafNs;
                return;
            }

            // Create intermediate namespace node
            node = new ApiNode
            {
                Id = fullName,
                Name = part,
                Kind = ApiNodeKind.Namespace,
                Children = [],
            };
            siblingMap[part] = node;
        }
        else if (isLeaf)
        {
            // Merge types from leafNs into existing node
            if (leafNs.Children is not null)
            {
                node.Children ??= [];
                foreach (var child in leafNs.Children)
                {
                    if (child.Kind != ApiNodeKind.Namespace)
                        node.Children.Add(child);
                }
            }
            return;
        }

        // Recurse into children
        node.Children ??= [];
        var childMap = new Dictionary<string, ApiNode>(StringComparer.Ordinal);
        foreach (var child in node.Children)
        {
            if (child.Kind == ApiNodeKind.Namespace)
                childMap[child.Name] = child;
        }

        var prevCount = childMap.Count;
        InsertNamespace(childMap, parts, depth + 1, leafNs);

        // Add any newly created child namespace nodes to the parent
        if (childMap.Count > prevCount)
        {
            foreach (var (name, child) in childMap)
            {
                if (!node.Children.Any(c => c.Kind == ApiNodeKind.Namespace && c.Name == name))
                    node.Children.Add(child);
            }
        }
    }
}
