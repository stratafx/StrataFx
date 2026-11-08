using System.Text;
using System.Text.Json;

namespace StrataFx.Model.Design;

/// <summary>
/// Writes changes into the text of a JSON layer file (model.json) without rewriting it: only the changed values are
/// touched, so comments, formatting, key order and anything the designer doesn't manage stay exactly as they were.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A changed value is replaced in place.</item>
/// <item>A new value is inserted among its siblings in a predictable place: entities and properties in model order,
/// settings in the order of <see cref="ModelSettings"/>, before the first sibling that sorts after it. It takes the
/// indentation of its siblings; a one-line object (<c>{ "visibleInList": false }</c>) stays on one line.</item>
/// <item>A removed value is deleted with its line and its comma; a comment on the same line goes with it, comments on
/// lines above stay. Entity and property objects that become empty (and hold no comments) are removed too.</item>
/// </list>
/// </remarks>
public static class JsonLayerText
{
    /// <summary>The text for a new, empty layer file.</summary>
    public const string Empty = "{\n}\n";

    /// <summary>Applies the changes to <paramref name="text"/> (null or empty starts a new file) and returns the new text.</summary>
    /// <exception cref="JsonException">The text is not valid JSON, or its root is not an object.</exception>
    public static string Apply(string? text, IEnumerable<ModelLayerChange> changes, AppModel model)
    {
        var editor = new Editor(string.IsNullOrWhiteSpace(text) ? Empty : text, model);
        foreach (var change in changes)
        {
            var path = PathOf(change.Key);
            if (change.IsSet) editor.Set(path, ModelLayerDocument.ToJsonLiteral(change.NewValue));
            else editor.Remove(path);
        }
        var result = editor.Text;
        using (JsonDocument.Parse(result, ModelLayerDocument.ReadOptions)) { } // never write a broken file
        return result;
    }

    private static string[] PathOf(ModelValueKey key) => key.Scope switch
    {
        ModelSettingScope.App => [key.Setting],
        ModelSettingScope.Entity => ["entities", key.Entity!, key.Setting],
        _ => ["entities", key.Entity!, "properties", key.Property!, key.Setting],
    };

    // ---- a small JSON parser that keeps positions (with comments and trailing commas, like JSON layers allow) ----

    private abstract class Node
    {
        public int Start;
        public int End; // exclusive
    }

    private sealed class ValueNode : Node;

    private sealed class ObjectNode : Node
    {
        public readonly List<Member> Members = [];
    }

    private sealed class Member
    {
        public string Name = "";
        public int NameStart;
        public Node Value = null!;
        public int Comma = -1;
    }

    private sealed class Parser(string s)
    {
        private int _i;
        public readonly List<(int Start, int End)> Comments = [];

        public ObjectNode ParseRoot()
        {
            SkipTrivia();
            if (_i >= s.Length || s[_i] != '{') throw new JsonException("A model layer must be a JSON object.");
            var root = (ObjectNode)ParseValue();
            SkipTrivia();
            return root;
        }

        private Node ParseValue()
        {
            if (_i >= s.Length) throw Error("Unexpected end of JSON.");
            switch (s[_i])
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"':
                    var start = _i;
                    ReadString();
                    return new ValueNode { Start = start, End = _i };
                default:
                    var begin = _i;
                    while (_i < s.Length && !char.IsWhiteSpace(s[_i]) && s[_i] is not (',' or '}' or ']' or '/')) _i++;
                    if (_i == begin) throw Error($"Unexpected '{s[_i]}'.");
                    return new ValueNode { Start = begin, End = _i };
            }
        }

        private ObjectNode ParseObject()
        {
            var node = new ObjectNode { Start = _i };
            _i++; // {
            SkipTrivia();
            while (_i < s.Length && s[_i] != '}')
            {
                if (s[_i] != '"') throw Error("Expected a property name.");
                var member = new Member { NameStart = _i };
                ReadString();
                member.Name = JsonSerializer.Deserialize<string>(s[member.NameStart.._i]) ?? "";
                SkipTrivia();
                if (_i >= s.Length || s[_i] != ':') throw Error("Expected ':'.");
                _i++;
                SkipTrivia();
                member.Value = ParseValue();
                SkipTrivia();
                if (_i < s.Length && s[_i] == ',')
                {
                    member.Comma = _i++;
                    SkipTrivia();
                }
                else if (_i < s.Length && s[_i] != '}') throw Error("Expected ',' or '}'.");
                node.Members.Add(member);
            }
            if (_i >= s.Length) throw Error("Unterminated object.");
            node.End = ++_i;
            return node;
        }

        private ValueNode ParseArray()
        {
            var node = new ValueNode { Start = _i };
            _i++; // [
            SkipTrivia();
            while (_i < s.Length && s[_i] != ']')
            {
                ParseValue();
                SkipTrivia();
                if (_i < s.Length && s[_i] == ',') { _i++; SkipTrivia(); }
            }
            if (_i >= s.Length) throw Error("Unterminated array.");
            node.End = ++_i;
            return node;
        }

        /// <summary>Reads a string literal and returns the index after it.</summary>
        private int ReadString()
        {
            _i++; // opening quote
            while (_i < s.Length && s[_i] != '"') _i += s[_i] == '\\' ? 2 : 1;
            if (_i >= s.Length) throw Error("Unterminated string.");
            return ++_i;
        }

        private void SkipTrivia()
        {
            while (_i < s.Length)
            {
                if (char.IsWhiteSpace(s[_i])) _i++;
                else if (s[_i] == '/' && _i + 1 < s.Length && s[_i + 1] == '/')
                {
                    var start = _i;
                    while (_i < s.Length && s[_i] != '\n') _i++;
                    Comments.Add((start, _i));
                }
                else if (s[_i] == '/' && _i + 1 < s.Length && s[_i + 1] == '*')
                {
                    var start = _i;
                    var end = s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    _i = end < 0 ? s.Length : end + 2;
                    Comments.Add((start, _i));
                }
                else break;
            }
        }

        private JsonException Error(string message) => new($"{message} (at character {_i}).");
    }

    // ---- the editor ----

    private sealed class Editor(string text, AppModel model)
    {
        public string Text { get; private set; } = text;

        private string NewLine => Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        private (ObjectNode Root, List<(int Start, int End)> Comments) Parse()
        {
            var parser = new Parser(Text);
            return (parser.ParseRoot(), parser.Comments);
        }

        public void Set(string[] path, string literal)
        {
            var (root, comments) = Parse();
            var obj = root;
            for (var depth = 0; depth < path.Length; depth++)
            {
                var member = Find(obj, path[depth]);
                if (member is null)
                {
                    if (NeedsExpanding(obj, path, depth, comments))
                    {
                        Expand(obj);
                        Set(path, literal);
                        return;
                    }
                    Insert(obj, path, depth, literal, comments);
                    return;
                }
                if (depth == path.Length - 1)
                {
                    Replace(member.Value.Start, member.Value.End, literal);
                    return;
                }
                if (member.Value is not ObjectNode child)
                {
                    // Not an object where one is expected (a JSON layer would fail on it anyway): replace it.
                    Replace(member.Value.Start, member.Value.End, BuildValue(path, depth + 1, literal, IndentAt(member.NameStart)));
                    return;
                }
                obj = child;
            }
        }

        public void Remove(string[] path)
        {
            var (root, _) = Parse();
            var chain = Walk(root, path);
            if (chain is null) return;
            RemoveMember(chain[^1].Owner, chain[^1].Member);

            // Remove entity and property objects (and "properties" / "entities") left empty.
            for (var length = path.Length - 1; length >= 1; length--)
            {
                var (newRoot, comments) = Parse();
                var containers = Walk(newRoot, path[..length]);
                if (containers is null) return;
                var (owner, member) = containers[^1];
                if (member.Value is not ObjectNode container || container.Members.Count > 0 || HasComments(container, comments)) return;
                RemoveMember(owner, member);
            }
        }

        private static List<(ObjectNode Owner, Member Member)>? Walk(ObjectNode root, string[] path)
        {
            var chain = new List<(ObjectNode, Member)>();
            var obj = root;
            for (var depth = 0; depth < path.Length; depth++)
            {
                var member = Find(obj, path[depth]);
                if (member is null) return null;
                chain.Add((obj, member));
                if (depth < path.Length - 1)
                {
                    if (member.Value is not ObjectNode child) return null;
                    obj = child;
                }
            }
            return chain;
        }

        /// <summary>Names match ignoring case, like JSON layers; with duplicates the last one wins, as when the layer is read.</summary>
        private static Member? Find(ObjectNode obj, string name) =>
            obj.Members.LastOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        // ---- inserting ----

        private void Insert(ObjectNode obj, string[] path, int depth, string literal, List<(int Start, int End)> comments)
        {
            var name = path[depth];
            var singleLine = IsSingleLine(obj) && obj.Members.Count > 0;
            var indent = obj.Members.Count > 0 && !singleLine ? IndentAt(obj.Members[0].NameStart) : IndentAt(obj.Start) + Unit;
            var member = ModelLayerDocument.ToJsonLiteral(name) + ": " + BuildValue(path, depth + 1, literal, indent);
            var nl = NewLine;

            if (obj.Members.Count == 0)
            {
                var close = obj.End - 1;
                var inner = Text[(obj.Start + 1)..close];
                if (!HasComments(obj, comments))
                {
                    var multiLine = inner.Contains('\n') || member.Contains('\n') || depth == 0;
                    Replace(obj.Start + 1, close, multiLine ? nl + indent + member + nl + IndentAt(obj.Start) : " " + member + " ");
                }
                else if (OnlyWhitespaceBefore(close)) InsertText(LineStart(close), indent + member + nl);
                else InsertText(close, nl + indent + member + nl + IndentAt(obj.Start));
                return;
            }

            var rank = Rank(path, depth, name);
            var before = rank is null ? null : obj.Members.FirstOrDefault(m => Rank(path, depth, m.Name) is { } r && r > rank);

            if (singleLine)
            {
                if (before is not null) InsertText(before.NameStart, member + ", ");
                else if (obj.Members[^1] is { Comma: >= 0 } trailing) InsertText(trailing.Comma + 1, " " + member);
                else InsertText(obj.Members[^1].Value.End, ", " + member);
                return;
            }

            if (before is not null && OnlyWhitespaceBefore(before.NameStart))
            {
                // Above the sibling, and above the comment lines that describe it.
                var at = LineStart(before.NameStart);
                while (at > obj.Start + 1)
                {
                    var previous = LineStart(at - 1);
                    if (previous <= obj.Start) break;
                    var line = Text[previous..at].Trim();
                    if (!line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith("/*", StringComparison.Ordinal)) break;
                    at = previous;
                }
                InsertText(at, indent + member + "," + nl);
                return;
            }
            if (before is not null)
            {
                InsertText(before.NameStart, member + ", ");
                return;
            }

            var last = obj.Members[^1];
            if (last.Comma >= 0)
            {
                var (at, endOfLine) = EndOfLine(last.Comma + 1);
                InsertText(endOfLine ? at : last.Comma + 1, nl + indent + member + ",");
            }
            else
            {
                var (at, endOfLine) = EndOfLine(last.Value.End);
                if (endOfLine && at > last.Value.End)
                {
                    InsertText(at, nl + indent + member); // after a comment on the same line
                    InsertText(last.Value.End, ",");
                }
                else InsertText(last.Value.End, "," + nl + indent + member);
            }
        }

        /// <summary>A one-line object that must hold a nested object is spread over lines first (when it has no comments).</summary>
        private bool NeedsExpanding(ObjectNode obj, string[] path, int depth, List<(int Start, int End)> comments) =>
            obj.Members.Count > 0 && IsSingleLine(obj) && path.Length - depth > 2 && !HasComments(obj, comments);

        private void Expand(ObjectNode obj)
        {
            var indent = IndentAt(obj.Start);
            var nl = NewLine;
            var builder = new StringBuilder("{");
            for (var i = 0; i < obj.Members.Count; i++)
            {
                var m = obj.Members[i];
                builder.Append(nl).Append(indent).Append(Unit).Append(Text[m.NameStart..m.Value.End]);
                if (i < obj.Members.Count - 1) builder.Append(',');
            }
            builder.Append(nl).Append(indent).Append('}');
            Replace(obj.Start, obj.End, builder.ToString());
        }

        /// <summary>
        /// The value for path[from..]: the literal itself, a one-line object for a single setting
        /// (<c>{ "caption": "Qty" }</c>), or nested objects over several lines.
        /// </summary>
        private string BuildValue(string[] path, int from, string literal, string indent)
        {
            if (from == path.Length) return literal;
            var name = ModelLayerDocument.ToJsonLiteral(path[from]);
            if (from == path.Length - 1) return "{ " + name + ": " + literal + " }";
            var nl = NewLine;
            return "{" + nl + indent + Unit + name + ": " + BuildValue(path, from + 1, literal, indent + Unit) + nl + indent + "}";
        }

        /// <summary>Where a name sorts among its siblings; null for names the designer doesn't manage (left where they are).</summary>
        private int? Rank(string[] path, int depth, string name)
        {
            switch (depth)
            {
                case 0:
                    return name.Equals(ModelSettings.TitleKey, StringComparison.OrdinalIgnoreCase) ? 0
                        : name.Equals("entities", StringComparison.OrdinalIgnoreCase) ? 1 : null;
                case 1:
                    return model.FindEntity(name) is not null ? ModelLayerDocument.EntityRank(model, name) : null;
                case 2:
                    if (name.Equals("properties", StringComparison.OrdinalIgnoreCase)) return 1000;
                    return ModelSettings.Find(ModelSettingScope.Entity, name) is { } entitySetting
                        ? ModelLayerDocument.SettingRank(new ModelValueKey(path[1], null, entitySetting.Key))
                        : null;
                case 3:
                    return model.FindEntity(path[1])?.FindProperty(name) is not null ? ModelLayerDocument.PropertyRank(model, path[1], name) : null;
                default:
                    return ModelSettings.Find(ModelSettingScope.Property, name) is { } propertySetting
                        ? ModelLayerDocument.SettingRank(new ModelValueKey(path[1], path[3], propertySetting.Key))
                        : null;
            }
        }

        // ---- removing ----

        private void RemoveMember(ObjectNode obj, Member m)
        {
            var index = obj.Members.IndexOf(m);
            var previous = index > 0 ? obj.Members[index - 1] : null;
            var after = m.Comma >= 0 ? m.Comma + 1 : m.Value.End;

            if (!IsSingleLine(obj) && OnlyWhitespaceBefore(m.NameStart) && EndOfLine(after) is (var eol, true))
            {
                // The member has its own line(s): delete them, with a comment at the end of the line.
                var end = eol < Text.Length && Text[eol] == '\r' ? eol + 1 : eol;
                if (end < Text.Length && Text[end] == '\n') end++;
                Delete(LineStart(m.NameStart), end);
                if (m.Comma < 0 && previous is { Comma: >= 0 }) Delete(previous.Comma, previous.Comma + 1);
                return;
            }

            if (obj.Members.Count == 1)
            {
                Replace(obj.Start + 1, obj.End - 1, "");
                return;
            }
            if (m.Comma >= 0) Delete(m.NameStart, SkipSpaces(m.Comma + 1));
            else if (previous is { Comma: >= 0 }) Delete(previous.Comma, m.Value.End);
            else Delete(m.NameStart, m.Value.End);
        }

        // ---- text helpers ----

        private string Unit
        {
            get
            {
                var (root, _) = Parse();
                if (root.Members.Count > 0 && OnlyWhitespaceBefore(root.Members[0].NameStart))
                {
                    var indent = IndentAt(root.Members[0].NameStart);
                    var outer = IndentAt(root.Start);
                    if (indent.Length > outer.Length) return indent[outer.Length..];
                }
                return "  ";
            }
        }

        private int LineStart(int position)
        {
            var newline = position == 0 ? -1 : Text.LastIndexOf('\n', position - 1);
            return newline + 1;
        }

        private string IndentAt(int position)
        {
            var start = LineStart(position);
            var end = start;
            while (end < Text.Length && Text[end] is ' ' or '\t') end++;
            return Text[start..end];
        }

        private bool OnlyWhitespaceBefore(int position) => Text[LineStart(position)..position].All(c => c is ' ' or '\t');

        private bool IsSingleLine(ObjectNode obj) => Text.IndexOf('\n', obj.Start, obj.End - obj.Start) < 0;

        private static bool HasComments(ObjectNode obj, List<(int Start, int End)> comments) =>
            comments.Any(c => c.Start > obj.Start && c.Start < obj.End);

        private int SkipSpaces(int position)
        {
            while (position < Text.Length && Text[position] is ' ' or '\t') position++;
            return position;
        }

        /// <summary>
        /// From <paramref name="position"/>, skips spaces and a comment on the same line. Returns the position of the line
        /// break (or the end of the text) and true, or the original position and false when more JSON follows on the line.
        /// </summary>
        private (int Position, bool EndOfLine) EndOfLine(int position)
        {
            var i = SkipSpaces(position);
            if (i + 1 < Text.Length && Text[i] == '/' && Text[i + 1] == '*')
            {
                var close = Text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close >= 0 && Text.IndexOf('\n', i, close - i) < 0) i = SkipSpaces(close + 2);
            }
            if (i + 1 < Text.Length && Text[i] == '/' && Text[i + 1] == '/')
                while (i < Text.Length && Text[i] != '\n') i++;
            if (i >= Text.Length) return (Text.Length, true);
            if (Text[i] == '\n') return (i > 0 && Text[i - 1] == '\r' ? i - 1 : i, true);
            if (Text[i] == '\r') return (i, true);
            return (position, false);
        }

        private void Replace(int start, int end, string value) => Text = string.Concat(Text.AsSpan(0, start), value, Text.AsSpan(end));
        private void InsertText(int position, string value) => Replace(position, position, value);
        private void Delete(int start, int end) => Replace(start, end, "");
    }
}
