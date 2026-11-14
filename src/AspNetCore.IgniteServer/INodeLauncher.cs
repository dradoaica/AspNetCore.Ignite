using Apache.Ignite.Core;

namespace AspNetCore.IgniteServer;

/// <summary>Starts and stops Ignite nodes: <see cref="Ignition" />, or a fake in the node lifecycle tests.</summary>
internal interface INodeLauncher
{
    IIgnite Start(IgniteConfiguration configuration);

    /// <summary>Stops the node of that name, cancelling its jobs or waiting for them; it raises the node's Stopped event.</summary>
    bool Stop(string name, bool cancel);
}

internal sealed class IgnitionNodeLauncher : INodeLauncher
{
    public static readonly IgnitionNodeLauncher Instance = new();

    public IIgnite Start(IgniteConfiguration configuration) => Ignition.Start(configuration);

    public bool Stop(string name, bool cancel) => Ignition.Stop(name, cancel);
}
