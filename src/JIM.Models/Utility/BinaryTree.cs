using JIM.Models.Core;

namespace JIM.Models.Utility
{
    /// <summary>
    /// Used to help establish hierarchical relationships between objects, i.e. managers/direct reports
    /// </summary>
    public class BinaryTree
    {
        public MetaverseObject MetaverseObject = null!;
        public BinaryTree Left = null!;
        public BinaryTree Right = null!;

        public BinaryTree(List<MetaverseObject> values) : this(values, 0) { }

        public BinaryTree(List<MetaverseObject> values, int index)
        {
            MetaverseObject = values[index];
            if (index * 2 + 1 < values.Count)
                Left = new BinaryTree(values, index * 2 + 1);

            if (index * 2 + 2 < values.Count)
                Right = new BinaryTree(values, index * 2 + 2);
        }
    }
}
