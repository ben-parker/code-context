
using System.Collections.Generic;

namespace CodeContext.Core
{
    public class CodeGraph
    {
        public List<CodeNode> Nodes { get; set; } = new List<CodeNode>();
        public List<CodeEdge> Edges { get; set; } = new List<CodeEdge>();
    }
}
