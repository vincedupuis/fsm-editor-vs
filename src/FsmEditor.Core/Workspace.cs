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
        private const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789;,/?:@&=+$-_.!~*'()#";

        /// <summary>Percent-encodes a relative path the way URI references are written (like JavaScript's encodeURI).</summary>
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

        public static string DecodeUri(string s)
        {
            try
            {
                return Uri.UnescapeDataString(s);
            }
            catch (UriFormatException)
            {
                return s;
            }
        }

        /// <summary>Relative path with <c>/</c> separators from directory <paramref name="fromDir"/> to <paramref name="to"/>.</summary>
        public static string Relative(string fromDir, string to)
        {
            var a = Split(Path.GetFullPath(fromDir));
            var b = Split(Path.GetFullPath(to));
            int i = 0;
            while (i < a.Length && i < b.Length && string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) i++;
            return string.Join("/", Enumerable.Repeat("..", a.Length - i).Concat(b.Skip(i)));
        }

        private static string[] Split(string p) => p.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>href of machine <paramref name="machineId"/> in <paramref name="targetFile"/>, as seen from <paramref name="documentFile"/>.</summary>
        public static string For(string documentFile, string targetFile, string machineId) =>
            $"{EncodeUri(Relative(Path.GetDirectoryName(documentFile), targetFile))}#{machineId}";

        /// <summary>The file and element id an href points to, relative to <paramref name="documentFile"/>.</summary>
        public static (string File, string Id) Resolve(string documentFile, string href)
        {
            var hash = href.IndexOf('#');
            var file = hash >= 0 ? href.Substring(0, hash) : href;
            var id = hash >= 0 ? href.Substring(hash + 1) : "";
            var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(documentFile), DecodeUri(file).Replace('/', Path.DirectorySeparatorChar)));
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
                var (file, id) = Hrefs.Resolve(documentFile, href);
                var s = summaryOf(file);
                info[href] = s != null && s.Id == id
                    ? new SubmachineInfo { Found = true, Name = s.Name, File = display(file), Points = s.Points }
                    : new SubmachineInfo { Found = false };
            }
            return info;
        }
    }
}
