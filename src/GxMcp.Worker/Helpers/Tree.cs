using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Helpers
{
    public class Tree<T>
    {
        public T Root { get; set; }
        public List<Tree<T>> Children { get; set; } = new List<Tree<T>>();

        public Tree(T root)
        {
            Root = root;
        }

        public void AddChild(Tree<T> child)
        {
            Children.Add(child);
        }
    }
}
