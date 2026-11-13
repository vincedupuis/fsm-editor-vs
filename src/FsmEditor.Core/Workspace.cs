using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FsmEditor.Core
{
    /// <summary>Name, id and connection points of the machine in a file.</summary>
    public sealed class MachineSummary
    {
        public string Id { get; set; } = "sm";
        public string Name { get; set; } = "";
        public List<ConnectionPointInfo> Points { get; set; } = new List<ConnectionPointInfo>();

        public static MachineSummary Of(FsmModel m) => new MachineSummary { Id = m.Id, Name = m.Name, Points = m.MachineConnectionPoints() };

        /// <summary>The summary of a file's text, or null when it isn't a readable state machine.</summary>
        public static MachineSummary TryRead(string text)
        {
            try
            {
                return Of(Xmi.FromXmi(text));
            }
            catch (Exception e) when (e is XmlParseException || e is XmiException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// How state machine files reference each other: submachine states hold an
    /// XMI href such as <c>sub/Payment.fsm#sm</c>, relative to the referencing file.
    /// </summary>
    public static class Hrefs
    {
        private const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789;,/:@&=+$-_.!~*'()";

        /// <summary>
        /// Percent-encodes the file part of an href (encodeHref of the VS Code edition): like JavaScript's encodeURI,
        /// plus <c>#</c> and <c>?</c>, which would otherwise end the path (a file named <c>a#b.fsm</c> is <c>a%23b.fsm</c>).
        /// </summary>
        public static string EncodeUri(string s)
        {
            var sb = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(s))
            {
                var c = (char)b;
                if (b < 0x80 && Unreserved.IndexOf(c) >= 0) sb.Append(c);
                else sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Like JavaScript's decodeURI: decodes <c>%XX</c> escapes (UTF-8), except those of reserved characters
        /// (<c>;/:@&amp;=+$,</c>; unlike decodeURI, <c>%23</c> and <c>%3F</c> give <c>#</c> and <c>?</c>, as
        /// <see cref="EncodeUri"/> writes them), and keeps malformed ones as they are.
        /// </summary>
        public static string DecodeUri(string s)
        {
            if (s.IndexOf('%') < 0) return s;
            int At(int i) =>
                i + 2 < s.Length && s[i] == '%' && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])
                    ? Convert.ToInt32(s.Substring(i + 1, 2), 16)
                    : -1;
            var sb = new StringBuilder();
            var i = 0;
            while (i < s.Length)
            {
                var b = At(i);
                if (b >= 0)
                {
                    var len = b < 0x80 ? 1 : b >= 0xC0 && b <= 0xDF ? 2 : b >= 0xE0 && b <= 0xEF ? 3 : b >= 0xF0 && b <= 0xF7 ? 4 : 0;
                    var run = new List<byte> { (byte)b };
                    var j = i + 3;
                    while (run.Count < len && At(j) >= 0)
                    {
                        run.Add((byte)At(j));
                        j += 3;
                    }
                    string text = null;
                    if (run.Count == len)
                    {
                        try
                        {
                            text = StrictUtf8.GetString(run.ToArray());
                        }
                        catch (DecoderFallbackException)
                        {
                            text = null;
                        }
                    }
                    if (text != null)
                    {
                        sb.Append(len == 1 && ";/:@&=+$,".IndexOf(text[0]) >= 0 ? s.Substring(i, j - i) : text);
                        i = j;
                        continue;
                    }
                }
                sb.Append(s[i]);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>Relative path with <c>/</c> separators from directory <paramref name="fromDir"/> to <paramref name="to"/>.</summary>
        public static string Relative(string fromDir, string to) => RelativeOfFull(Path.GetFullPath(fromDir), Path.GetFullPath(to));

        /// <summary>
        /// <see cref="Relative"/> of two full paths, computed as the VS Code edition does on the paths of their file
        /// URIs: a drive is the first segment (<c>d:</c>, lower case) and a UNC server is not a segment. So a file on
        /// another drive is reached by climbing to the root, <c>../../e:/models/M.fsm</c>, which
        /// <see cref="JoinFull"/> resolves.
        /// </summary>
        public static string RelativeOfFull(string fromDir, string to)
        {
            var a = UriPath(fromDir).Segments;
            var b = UriPath(to).Segments;
            int i = 0;
            while (i < a.Count && i < b.Count && string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) i++;
            return string.Join("/", Enumerable.Repeat("..", a.Count - i).Concat(b.Skip(i)));
        }

        /// <summary>
        /// Full directory <paramref name="dir"/> joined with the relative path <paramref name="relative"/>, as VS Code
        /// joins file URI paths: <c>..</c> stops at the root, and a drive segment met there (<c>../../e:/M.fsm</c>)
        /// starts that drive.
        /// </summary>
        public static string JoinFull(string dir, string relative)
        {
            var (server, segments, windows) = UriPath(dir);
            foreach (var s in relative.Split(windows ? new[] { '/', '\\' } : new[] { '/' }))
            {
                if (s.Length == 0 || s == ".") continue;
                if (s == "..")
                {
                    if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                }
                else segments.Add(s);
            }
            if (server != null) return @"\\" + server + @"\" + string.Join(@"\", segments);
            if (segments.Count > 0 && IsDrive(segments[0])) return segments[0].ToUpperInvariant() + @"\" + string.Join(@"\", segments.Skip(1));
            return windows ? @"\" + string.Join(@"\", segments) : "/" + string.Join("/", segments);
        }

        private static bool IsDrive(string s) => s.Length == 2 && s[1] == ':' && (s[0] >= 'A' && s[0] <= 'Z' || s[0] >= 'a' && s[0] <= 'z');

        /// <summary>A full path as a file URI path: the UNC server (null for none), and the segments, the first one a lower-case drive.</summary>
        private static (string Server, List<string> Segments, bool Windows) UriPath(string full)
        {
            var unc = full.StartsWith(@"\\", StringComparison.Ordinal);
            var windows = unc || full.Length >= 2 && IsDrive(full.Substring(0, 2));
            var segments = (unc ? full.Substring(2) : full).Split(windows ? new[] { '/', '\\' } : new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            string server = null;
            if (unc && segments.Count > 0)
            {
                server = segments[0];
                segments.RemoveAt(0);
            }
            else if (windows && segments.Count > 0) segments[0] = segments[0].ToLowerInvariant();
            return (server, segments, windows);
        }

        /// <summary>href of machine <paramref name="machineId"/> in <paramref name="targetFile"/>, as seen from <paramref name="documentFile"/>.</summary>
        public static string For(string documentFile, string targetFile, string machineId) =>
            $"{EncodeUri(Relative(Path.GetDirectoryName(documentFile), targetFile))}#{machineId}";

        /// <summary>The file and element id an href points to, relative to <paramref name="documentFile"/>.</summary>
        public static (string File, string Id) Resolve(string documentFile, string href)
        {
            var hash = href.IndexOf('#');
            var file = hash >= 0 ? href.Substring(0, hash) : href;
            // As href.split('#') in the other editions: the id ends at a second #.
            var id = hash >= 0 ? href.Substring(hash + 1).Split('#')[0] : "";
            var path = Path.GetFullPath(JoinFull(Path.GetFullPath(Path.GetDirectoryName(documentFile)), DecodeUri(file)));
            return (path, id);
        }
    }

    /// <summary>Works out which machines a document can use as submachines, and what its submachine states refer to.</summary>
    public static class SubmachineResolver
    {
        /// <param name="documentFile">The document being edited.</param>
        /// <param name="candidates">Other *.fsm files of the workspace.</param>
        /// <param name="summaryOf">Reads a file's summary (null when missing or unreadable).</param>
        /// <param name="display">Path shown to the user.</param>
        public static List<MachineInfo> ListMachines(string documentFile, IEnumerable<string> candidates, Func<string, MachineSummary> summaryOf, Func<string, string> display)
        {
            var output = new List<MachineInfo>();
            foreach (var file in candidates)
            {
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(documentFile), StringComparison.OrdinalIgnoreCase)) continue;
                var s = summaryOf(file);
                if (s == null) continue;
                output.Add(new MachineInfo { Href = Hrefs.For(documentFile, file, s.Id), Name = s.Name, File = display(file), Points = s.Points });
            }
            return output.OrderBy(m => m.Name, StringComparer.CurrentCulture).ToList();
        }

        /// <summary>Reads the machines referenced by submachine states, to learn their names and connection points.</summary>
        public static Dictionary<string, SubmachineInfo> Resolve(string documentFile, FsmModel model, IReadOnlyList<MachineInfo> machines, Func<string, MachineSummary> summaryOf, Func<string, string> display)
        {
            var info = new Dictionary<string, SubmachineInfo>();
            foreach (var href in model.Vertices.Where(v => v.Type == VertexType.State && v.Submachine.Length > 0).Select(v => v.Submachine).Distinct())
            {
                var known = machines.FirstOrDefault(m => m.Href == href);
                if (known != null)
                {
                    info[href] = new SubmachineInfo { Found = true, Name = known.Name, File = known.File, Points = known.Points };
                    continue;
                }
                // Outside the workspace, or written with a different relative path.
                string file, id;
                try
                {
                    (file, id) = Hrefs.Resolve(documentFile, href);
                }
                catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
                {
                    // Not a file path (characters a path can't hold): a machine that can't be found.
                    info[href] = new SubmachineInfo { Found = false };
                    continue;
                }
                var s = summaryOf(file);
                info[href] = s != null && s.Id == id
                    ? new SubmachineInfo { Found = true, Name = s.Name, File = display(file), Points = s.Points }
                    : new SubmachineInfo { Found = false };
            }
            return info;
        }
    }
}
