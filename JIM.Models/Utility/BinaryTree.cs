using JIM.Models.Core;

namespace JIM.Models.Utility
{
    /// <summary>
    /// Used to help establish hierarchical relationships between objects, i.e. managers/direct reports
    /// </summary>
    public class BinaryTree
    {
        public readonly MetaverseObject MetaverseObject;
        public readonly BinaryTree? Left;
        public readonly BinaryTree? Right;

        public BinaryTree(IReadOnlyList<MetaverseObject> values, int index = 0)
        {
            MetaverseObject = values[index];
            if (index * 2 + 1 < values.Count)
                Left = new BinaryTree(values, index * 2 + 1);

            if (index * 2 + 2 < values.Count)
                Right = new BinaryTree(values, index * 2 + 2);
        }
    }
}
