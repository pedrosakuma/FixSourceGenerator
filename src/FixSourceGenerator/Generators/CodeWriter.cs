using System.Text;

namespace FixSourceGenerator.Generators
{
    /// <summary>Minimal indentation-aware source builder used by the generators.</summary>
    internal sealed class CodeWriter
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private int _indent;

        public CodeWriter Open(string line)
        {
            Line(line);
            Line("{");
            _indent++;
            return this;
        }

        public CodeWriter Close()
        {
            _indent--;
            Line("}");
            return this;
        }

        public CodeWriter Line(string line = "")
        {
            if (line.Length == 0)
            {
                _sb.Append('\n');
                return this;
            }

            for (int i = 0; i < _indent; i++)
            {
                _sb.Append("    ");
            }

            _sb.Append(line);
            _sb.Append('\n');
            return this;
        }

        public override string ToString() => _sb.ToString();
    }
}
