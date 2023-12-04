namespace Nethermind.Trie
{
    public readonly struct NodeCommitInfo
    {
        public NodeCommitInfo(TrieNode node, TreePath path)
        {
            ChildPositionAtParent = 0;
            Node = node;
            Path = path;
            NodeParent = null;
        }

        public NodeCommitInfo(
            TrieNode node,
            TrieNode nodeParent,
            TreePath path,
            int childPositionAtParent)
        {
            ChildPositionAtParent = childPositionAtParent;
            Node = node;
            Path = path;
            NodeParent = nodeParent;
        }

        public TrieNode? Node { get; }
        public TreePath Path { get; }

        public TrieNode? NodeParent { get; }

        public int ChildPositionAtParent { get; }

        public bool IsEmptyBlockMarker => Node is null;

        public bool IsRoot => !IsEmptyBlockMarker && NodeParent is null;

        public override string ToString()
        {
            return $"[{nameof(NodeCommitInfo)}|{Node}|{(NodeParent is null ? "root" : $"child {ChildPositionAtParent} of {NodeParent}")}]";
        }
    }
}
