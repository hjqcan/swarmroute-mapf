/***
 * Detects if a given graph is cyclic. Supports directed and undirected graphs.
 */

using System;
using System.Collections.Generic;
using AJR.Platform.Algorithms.Common;
using AJR.Platform.Algorithms.DataStructures.Graphs;

namespace AJR.Platform.Algorithms.Graphs
{
    /// <summary>
    /// Implements Cycles Detection in Graphs
    /// </summary>
    public static class CyclesDetector
    {
        /// <summary>
        /// [Undirected DFS Forest].
        /// Helper function used to decide whether the graph explored from a specific vertex contains a cycle.
        /// </summary>
        /// <param name="graph">The graph to explore.</param>
        /// <param name="source">The vertex to explore graph from.</param>
        /// <param name="parent">The predecessor node to the vertex we are exploring the graph from.</param>
        /// <param name="visited">A hash set of the explored nodes so far.</param>
        /// <returns>True if there is a cycle; otherwise, false.</returns>
        private static bool _isUndirectedCyclic<T>(IGraph<T> graph, T source, object parent, ref HashSet<T> visited) where T : IComparable<T>
        {
            if (!visited.Contains(source))
            {
                // Mark the current node as visited
                visited.Add(source);

                // Recur for all the vertices adjacent to this vertex
                foreach (var adjacent in graph.Neighbours(source))
                {
                    // If an adjacent node was not visited, then check the DFS forest of the adjacent for UNdirected cycles.
                    if (!visited.Contains(adjacent) && _isUndirectedCyclic<T>(graph, adjacent, source, ref visited))
                        return true;

                    // If an adjacent is visited and NOT parent of current vertex, then there is a cycle.
                    if (parent != (object)null && !adjacent.IsEqualTo((T)parent))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// [Directed DFS Forest]
        /// Helper function used to decide whether the graph explored from a specific vertex contains a cycle.
        /// </summary>
        /// <param name="graph">The graph to explore.</param>
        /// <param name="source">The vertex to explore graph from.</param>
        /// <param name="parent">The predecessor node to the vertex we are exploring the graph from.</param>
        /// <param name="visited">A hash set of the explored nodes so far.</param>
        /// <param name="recursionStack">A set of element that are currently being processed.</param>
        /// <returns>True if there is a cycle; otherwise, false.</returns>
        private static bool _isDirectedCyclic<T>(IGraph<T> graph, T source, ref HashSet<T> visited, ref HashSet<T> recursionStack) where T : IComparable<T>
        {
            if (!visited.Contains(source))
            {
                // Mark the current node as visited and add it to the recursion stack
                visited.Add(source);
                recursionStack.Add(source);

                // Recur for all the vertices adjacent to this vertex
                foreach (var adjacent in graph.Neighbours(source))
                {
                    // If an adjacent node was not visited, then check the DFS forest of the adjacent for directed cycles.
                    if (!visited.Contains(adjacent) && _isDirectedCyclic<T>(graph, adjacent, ref visited, ref recursionStack))
                        return true;

                    // If an adjacent is visited and is on the recursion stack then there is a cycle.
                    if (recursionStack.Contains(adjacent))
                        return true;
                }
            }

            // Remove the source vertex from the recursion stack
            recursionStack.Remove(source);
            return false;
        }

        /// <summary>
        /// Returns all cycle. Add by Liangqiliang 20230420
        /// </summary>
        public static List<List<T>> AllDirectedCyclics<T>(IGraph<T> Graph) where T : IComparable<T>
        {
            if (Graph == null)
                throw new ArgumentNullException();

            var visited = new List<T>();
            var recursionStack = new List<T>();
            var allCyclics = new List<List<T>>();

            if (Graph.IsDirected)
            {
                foreach (var vertex in Graph.Vertices)
                {
                    _findDirectedCyclic<T>(Graph, vertex, ref visited, ref recursionStack, ref allCyclics);

                    //重置
                    visited = new List<T>();
                    recursionStack = new List<T>();
                }
            }
            else
            {

            }

            return allCyclics;
        }

        /// <summary>
        /// Add by Liangqiliang 20230420
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="graph"></param>
        /// <param name="source"></param>
        /// <param name="visited"></param>
        /// <param name="recursionStack"></param>
        /// <param name="cyclics"></param>
        private static void _findDirectedCyclic<T>(IGraph<T> graph, T source, ref List<T> visited, ref List<T> recursionStack, ref List<List<T>> cyclics) where T : IComparable<T>
        {
            if (visited.Contains(source))
            {
                if (recursionStack != null && recursionStack.Count > 0)
                {
                    if ((recursionStack.IndexOf(source)) != -1)
                    {
                        var tempList = new List<T>();
                        for (int k = recursionStack.IndexOf(source); k < recursionStack.Count; k++)
                        {
                            tempList.Add(recursionStack[k]);
                        }

                        //添加环时，检查是否已经存在该环
                        var count = cyclics.Count(c => c.All(tempList.Contains) && c.Count == tempList.Count);
                        if (count == 0)
                        {
                            cyclics.Add(tempList);
                        }
                    }
                }
                return;
            }

            if (!visited.Contains(source))
            {
                // Mark the current node as visited and add it to the recursion stack
                visited.Add(source);
                recursionStack.Add(source);

                // Recur for all the vertices adjacent to this vertex
                var s = string.Join(",", graph.Neighbours(source));
                foreach (var adjacent in graph.Neighbours(source))
                {
                    _findDirectedCyclic<T>(graph, adjacent, ref visited, ref recursionStack, ref cyclics);
                }
            }

            // Remove the source vertex from the recursion stack
            recursionStack.Remove(source);
        }

        /// <summary>
        /// 获取以xxx字符串开头形成环的顶点 Add by Huangjianqin 20230410
        /// </summary>
        /// <param name="Graph"></param>
        /// <param name="vertexStartWith">顶点以xxx字符串开头</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static List<string> CyclicVertices(IGraph<string> Graph,string vertexStartWith="")
        {
            if (Graph == null)
                throw new ArgumentNullException();

            List<string> cyclicVertices = new List<string>();

            var visited = new HashSet<string>();
            var recursionStack = new HashSet<string>();

            var graphVertices = Graph.Vertices;
            if(!string.IsNullOrEmpty(vertexStartWith))
            {
                graphVertices = Graph.Vertices.Where(q => q.StartsWith(vertexStartWith));
            }

            if (Graph.IsDirected)
            {
                foreach (var vertex in graphVertices)
                {
                    if (_isDirectedCyclic<string>(Graph, vertex, ref visited, ref recursionStack))
                    {
                        cyclicVertices.Add(vertex);
                        visited = new HashSet<string>();
                        recursionStack = new HashSet<string>();
                    }
                }    
            }
            else
            {
                foreach (var vertex in Graph.Vertices)
                {
                    if (_isUndirectedCyclic<string>(Graph, vertex, null, ref visited))
                    {
                        cyclicVertices.Add(vertex);
                        visited = new HashSet<string>();
                        recursionStack = new HashSet<string>();
                    }
                }  
            }

            return cyclicVertices;
        }

        /// <summary>
        /// 获取所有形成环的顶点 Add by Huangjianqin 20230410
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="Graph"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static List<T> CyclicVertices<T>(IGraph<T> Graph) where T : IComparable<T>
        {
            if (Graph == null)
                throw new ArgumentNullException();

            List<T> cyclicVertices = new List<T>();

            var visited = new HashSet<T>();
            var recursionStack = new HashSet<T>();

            if (Graph.IsDirected)
            {
                foreach (var vertex in Graph.Vertices)
                {
                    if (_isDirectedCyclic<T>(Graph, vertex, ref visited, ref recursionStack))
                    {
                        cyclicVertices.Add(vertex);
                        visited = new HashSet<T>();
                        recursionStack = new HashSet<T>();
                    }
                }
            }
            else
            {
                foreach (var vertex in Graph.Vertices)
                {
                    if (_isUndirectedCyclic<T>(Graph, vertex, null, ref visited))
                    {
                        cyclicVertices.Add(vertex);
                        visited = new HashSet<T>();
                        recursionStack = new HashSet<T>();
                    }
                }
            }

            return cyclicVertices;
        }

        /// <summary>
        /// Returns true if Graph has cycle.
        /// </summary>
        public static bool IsCyclic<T>(IGraph<T> Graph) where T : IComparable<T>
        {
            if (Graph == null)
                throw new ArgumentNullException();

            var visited = new HashSet<T>();
            var recursionStack = new HashSet<T>();

            if (Graph.IsDirected)
            {
                foreach (var vertex in Graph.Vertices)
                    if (_isDirectedCyclic<T>(Graph, vertex, ref visited, ref recursionStack))
                        return true;
            }
            else
            {
                foreach (var vertex in Graph.Vertices)
                    if (_isUndirectedCyclic<T>(Graph, vertex, null, ref visited))
                        return true;
            }

            return false;
        }

    }

}
