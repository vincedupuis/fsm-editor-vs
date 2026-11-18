using System;
using System.Collections.Generic;
using System.Linq;

namespace FsmEditor.Core
{
    public sealed class LoadException : Exception
    {
        public LoadException(string message) : base(message) { }
    }

    /// <summary>
    /// Reading a machine's model from a file's text, in either format: the JSON format, or the XMI of earlier versions
    /// (read as it is, without converting it). fsm-editor-common's (<see cref="Common"/>).
    /// </summary>
    public static class Load
    {
        /// <summary>
        /// The model of a <c>.fsm</c> file's text, and of its layout file's text when given (else every element is
        /// placed automatically). Throws <see cref="LoadException"/> when the text can't be read (the first problem,
        /// with its line).
        /// </summary>
        public static FsmModel LoadModel(string text, string layout = null)
        {
            var r = (Dictionary<string, object>)Common.Call("load", Common.Args(("model", Common.Str(text ?? "")), ("layout", Common.Str(layout))));
            if (r.TryGetValue("error", out var error)) throw new LoadException(error as string ?? "");
            return ModelJson.Read(r["model"]);
        }

        /// <summary>
        /// The submachine paths of a model, relative to its file (<c>Payment.fsm</c>; an old file's hrefs without
        /// their <c>#id</c>, decoded: <paramref name="old"/>).
        /// </summary>
        public static List<string> SubmachineFiles(FsmModel model, bool old) =>
            model.Vertices.Where(v => v.Type == VertexType.State && v.Submachine.Length > 0)
                .Select(v => old ? Hrefs.DecodeUri(v.Submachine.Split('#')[0]) : v.Submachine)
                .ToList();

        /// <summary>The submachine paths of a file's text (either format); none when it can't be read.</summary>
        public static List<string> SubmachineFilesOf(string text)
        {
            var r = (Dictionary<string, object>)Common.Call("load", Common.Args(("model", Common.Str(text ?? ""))));
            return r.ContainsKey("error") ? new List<string>() : ModelJson.Strings(r, "submachines");
        }
    }
}
