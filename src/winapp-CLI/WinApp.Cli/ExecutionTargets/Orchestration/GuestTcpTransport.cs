// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Connection material handed to a guest agent through the read-only bootstrap folder
/// (spec §"Transport and command channel").
/// </summary>
/// <remarks>
/// Carried through a read-only mapped folder rather than the command line, because a command line is
/// visible to every process in the guest and the pre-shared key must not be. The folder is read-only
/// so a co-resident guest application cannot rewrite the material to redirect the agent.
/// <para>
/// A fresh key is generated per boot, so material recovered from an earlier generation authenticates
/// nothing.
/// </para>
/// </remarks>
internal sealed record GuestBootstrapMaterial
{
    /// <summary>Schema version, so a newer agent stays readable or fails closed.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Target the agent must agree it is serving.</summary>
    public required string TargetId { get; init; }

    /// <summary>Generation the agent must agree it is serving.</summary>
    public required string TargetEpoch { get; init; }

    /// <summary>Base64 per-boot pre-shared key.</summary>
    public required string PreSharedKey { get; init; }

    /// <summary>TCP port the agent listens on.</summary>
    public required int Port { get; init; }

    /// <summary>Current schema version emitted by this build.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>File name inside the bootstrap folder.</summary>
    public const string FileName = "connection.json";

    /// <summary>Generates fresh material for one boot.</summary>
    public static GuestBootstrapMaterial Create(ExecutionTargetRef target, ExecutionTargetEpoch epoch, int port) =>
        new()
        {
            SchemaVersion = CurrentSchemaVersion,
            TargetId = target.Id,
            TargetEpoch = epoch.Value,
            PreSharedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(GuestProtocol.PreSharedKeySize)),
            Port = port,
        };

    /// <summary>The decoded pre-shared key.</summary>
    /// <exception cref="ExecutionTargetException">The material is malformed.</exception>
    public byte[] DecodeKey()
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(PreSharedKey);
        }
        catch (FormatException ex)
        {
            throw Malformed(ex);
        }

        if (key.Length != GuestProtocol.PreSharedKeySize)
        {
            throw Malformed(innerException: null);
        }

        return key;
    }

    /// <summary>Serializes this material.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(this, GuestBootstrapJsonContext.Default.GuestBootstrapMaterial);

    /// <summary>Parses material, returning null for anything malformed or from an unknown schema.</summary>
    public static GuestBootstrapMaterial? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var material = JsonSerializer.Deserialize(json, GuestBootstrapJsonContext.Default.GuestBootstrapMaterial);
            return material?.SchemaVersion == CurrentSchemaVersion ? material : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ExecutionTargetException Malformed(Exception? innerException) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransportFailed,
            "The Windows Sandbox connection material is malformed.",
            userAction: "Retry the command so winapp recreates it.",
            innerException: innerException);
}

/// <summary>Source-generated serializer context for bootstrap material.</summary>
[JsonSerializable(typeof(GuestBootstrapMaterial))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class GuestBootstrapJsonContext : JsonSerializerContext
{
}

/// <summary>
/// An authenticated, encrypted framed TCP connection to a guest agent.
/// </summary>
/// <remarks>
/// TCP is used because Windows Sandbox exposes a guest IP and no lower-level channel; a future
/// backend with Hyper-V sockets or a remote API substitutes its own transport without any of the
/// command-channel semantics changing.
/// <para>
/// The listener is deliberately bound to the loopback-facing address the guest connects <em>to</em>
/// rather than all interfaces, and the pre-shared handshake means a connection from anywhere else
/// fails authentication before it can send a single operation.
/// </para>
/// </remarks>
internal static class GuestTcpTransport
{
    /// <summary>How long the host waits for the guest agent to accept a connection.</summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Delay between connection attempts while the agent is still starting.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Connects to a guest agent and completes the authenticated handshake, retrying while the
    /// agent is still coming up.
    /// </summary>
    /// <remarks>
    /// Retrying only covers connection refusal. A handshake that fails is never retried: it means
    /// the peer is not the agent winapp started, or is serving a different generation, and repeating
    /// the attempt would only turn a clear authentication failure into a timeout.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// The agent never accepted a connection, or failed to authenticate.
    /// </exception>
    public static async Task<IGuestTransport> ConnectAsync(
        string address,
        GuestBootstrapMaterial material,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(material);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        var socket = await ConnectSocketAsync(address, material.Port, timeout.Token, cancellationToken)
            .ConfigureAwait(false);

        var stream = new NetworkStream(socket, ownsSocket: true);

        try
        {
            return await GuestSecureChannel.EstablishAsync(
                stream,
                GuestRole.Host,
                material.DecodeKey(),
                material.TargetId,
                material.TargetEpoch,
                timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Listens for the host and completes the authenticated handshake, from inside the guest.
    /// </summary>
    /// <remarks>
    /// One connection at a time by design: the agent serves a single host channel, and accepting a
    /// second would let two hosts issue interleaved mutation operations against one guest with no
    /// coordination between them.
    /// </remarks>
    public static async Task<IGuestTransport> AcceptAsync(
        TcpListener listener,
        GuestBootstrapMaterial material,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(material);

        var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();

        try
        {
            return await GuestSecureChannel.EstablishAsync(
                stream,
                GuestRole.Guest,
                material.DecodeKey(),
                material.TargetId,
                material.TargetEpoch,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Binds a listener, letting the OS choose the port when none is fixed.</summary>
    /// <remarks>
    /// Ownership transfers to the caller on success. If <see cref="TcpListener.Start()"/> or reading
    /// the bound endpoint fails, the listener is disposed here — otherwise a failed bind would leak
    /// a socket, and the agent retries binding on the path that reports the failure.
    /// </remarks>
    /// <returns>The listener and the port it actually bound.</returns>
    public static (TcpListener Listener, int Port) Listen(int requestedPort)
    {
        var listener = new TcpListener(IPAddress.Any, requestedPort);

        try
        {
            listener.Start();
            return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    private static async Task<Socket> ConnectSocketAsync(
        string address,
        int port,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        while (true)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                await socket.ConnectAsync(IPAddress.Parse(address), port, timeoutToken).ConfigureAwait(false);
                return socket;
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                socket.Dispose();

                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.TransportFailed,
                    "The Windows Sandbox agent did not start accepting connections in time.",
                    userAction: "Retry the command. If it keeps failing, close Windows Sandbox and try again.",
                    context: new Dictionary<string, string>
                    {
                        ["guestAddress"] = address,
                        ["port"] = port.ToString(CultureInfo.InvariantCulture),
                    });
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            // The agent is still coming up. Only connection refusal reaches here; anything else has
            // already been rethrown.
            await Task.Delay(RetryDelay, timeoutToken).ConfigureAwait(false);
        }
    }
}
