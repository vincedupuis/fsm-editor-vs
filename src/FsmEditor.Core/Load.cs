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
    /// Reading a machine's model from a file's text, in either format: the JSON format (<see cref="Format"/>), or the
    /// XMI of earlier versions (<see cref="Xmi"/>, read as it is, without converting it). A port of the VS Code
    /// edition's <c>src/load.ts</c>.
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
            if (Format.IsOldFormat(text))
            {
                try
                {
                    return Xmi.FromXmi(text);
                }
                catch (Exception e) when (e is XmlParseException || e is XmiException)
                {
                    throw new LoadException(e.Message);
                }
            }
            var read = Format.ReadMachine(text, layout);
            if (read.Failed) throw new LoadException($"Line {read.Problems[0].Line}: {read.Problems[0].Message}");
            return read.Model;
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
            if (!Format.IsOldFormat(text))
            {
                // Paths only: no layout to apply.
                var read = Format.ReadModel(text);
                return read.Failed ? new List<string>() : SubmachineFiles(read.Model, false);
            }
            try
            {
                return SubmachineFiles(LoadModel(text), true);
            }
            catch (LoadException)
            {
                return new List<string>();
            }
        }
    }
}
