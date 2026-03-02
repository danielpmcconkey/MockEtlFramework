namespace Lib.Control;

/// <summary>
/// Builds a topologically sorted execution plan for a set of jobs.
/// </summary>
internal static class ExecutionPlan
{
    /// <summary>
    /// Returns jobs in an order that respects unsatisfied dependencies.
    ///
    /// A dependency is satisfied (and therefore ignored for ordering) when:
    ///   SameDay — always treated as unsatisfied (will be checked at execution time).
    ///   Latest  — the upstream job has ever succeeded for any run_date (it's in everSucceededIds).
    ///
    /// Throws InvalidOperationException if a dependency cycle is detected among unsatisfied edges.
    /// </summary>
    internal static List<JobRegistration> Build(
        List<JobRegistration> jobs,
        List<JobDependency>   deps,
        HashSet<int>          everSucceededIds)
    {
        // All requested jobs participate in the plan.
        var toRunById = jobs
            .ToDictionary(j => j.JobId);

        // Build adjacency list and in-degree map over only the ordering-relevant edges.
        var inDegree   = toRunById.Keys.ToDictionary(id => id, _ => 0);
        var downstream = toRunById.Keys.ToDictionary(id => id, _ => new List<int>());

        foreach (var dep in deps)
        {
            int upstreamId   = dep.DependsOnJobId;
            int downstreamId = dep.JobId;

            // Both jobs must be in the "to run" set for this edge to matter.
            if (!toRunById.ContainsKey(upstreamId) || !toRunById.ContainsKey(downstreamId))
                continue;

            // Check if the upstream has already satisfied this dependency.
            bool satisfied = dep.DependencyType == "SameDay"
                ? false  // SameDay deps are checked at execution time, not plan time
                : everSucceededIds.Contains(upstreamId);

            if (satisfied) continue;

            // Unsatisfied — this edge defines a real ordering constraint.
            downstream[upstreamId].Add(downstreamId);
            inDegree[downstreamId]++;
        }

        // Kahn's algorithm.
        var queue  = new Queue<int>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var result = new List<JobRegistration>();

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            result.Add(toRunById[id]);

            foreach (int nextId in downstream[id])
            {
                if (--inDegree[nextId] == 0)
                    queue.Enqueue(nextId);
            }
        }

        if (result.Count != toRunById.Count)
            throw new InvalidOperationException(
                "Cycle detected in the job dependency graph. Cannot build a valid execution plan.");

        return result;
    }
}
