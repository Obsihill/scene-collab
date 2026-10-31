using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SceneCollab.Contracts.Protocol;

namespace SceneCollab.V03Probe;

/// <summary>
/// The guest half of the Unity round trip: a non-Unity participant creates an object in somebody
/// else's scene, moves it, and deletes it — over the real server.
///
/// It is the only check that proves the structural op path end to end: the server has to relay the
/// op, the Unity applier has to build the GameObject, and the registry has to bind the id the guest
/// invented so later ops on the same id still resolve.
///
/// Coordination is by files in the shared directory, the same way the v0.2 harness does it.
/// </summary>
internal static class GuestFlow
{
    public static async Task<int> RunAsync(string dir, string url, string token, string room)
    {
        Console.WriteLine("[guest] waiting for the unity host description in " + dir);
        var hostFile = Path.Combine(dir, "v03-host.json");
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (!File.Exists(hostFile))
        {
            if (DateTime.UtcNow > deadline)
            {
                Console.WriteLine("[guest] FAIL: the unity host never wrote " + hostFile);
                return 1;
            }

            await Task.Delay(200);
        }

        var host = JsonDocument.Parse(await ReadShared(hostFile)).RootElement;
        var sceneGuid = host.GetProperty("sceneGuid").GetString()!;
        var sceneName = host.GetProperty("sceneName").GetString()!;
        var scenePath = host.GetProperty("scenePath").GetString()!;
        Console.WriteLine("[guest] host scene " + sceneName + " guid " + sceneGuid);

        var peer = await Peer.ConnectAsync(url, room, token, "v03-guest", sceneGuid, sceneName, scenePath);
        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length == 0 ? "" : "  — " + detail));
            if (!ok)
            {
                failures++;
            }
        }

        Check("guest joined the host's scene", peer.Welcome is { isHost: false }, "isHost=" + peer.Welcome?.isHost);

        // Ids are lowercase hex, the same shape the editor mints from a GlobalObjectId hash.
        var nodeId = Guid.NewGuid().ToString("N")[..16];

        await peer.SendAsync(new WireMessage
        {
            type = MessageTypes.Lock,
            protocolVersion = ProtocolVersion.Current,
            lockNodeId = nodeId,
        });

        var lockView = await peer.NextAsync(m => m.type == MessageTypes.Lock);
        Check("guest can claim a new node id", lockView?.lockedNodeIds?.Contains(nodeId) == true);

        var addId = Guid.NewGuid().ToString("N");
        await peer.SendAsync(new WireMessage
        {
            type = MessageTypes.Op,
            protocolVersion = ProtocolVersion.Current,
            opId = addId,
            opKind = OpKinds.AddNode,
            nodeId = nodeId,
            oldValue = "",
            // add_node carries a stand-in payload, not a bare name: the receiver rebuilds what it can.
            newValue = "{\"name\":\"Guest Cube\"}",
            parentNodeId = "",
            siblingIndex = 0,
            revision = peer.Revision,
        });

        var addAck = await peer.NextAsync(m => m.type == MessageTypes.Ack && m.opId == addId);
        Check("server accepted add_node", addAck is { accepted: true },
            "code=" + (addAck?.code ?? "(none)") + (addAck?.message == null ? "" : " " + addAck.message));

        await WriteShared(Path.Combine(dir, "v03-guest-added.json"),
            JsonSerializer.Serialize(new { nodeId, name = "Guest Cube" }));

        var moveId = Guid.NewGuid().ToString("N");
        await peer.SendAsync(new WireMessage
        {
            type = MessageTypes.Op,
            protocolVersion = ProtocolVersion.Current,
            opId = moveId,
            opKind = OpKinds.SetProperty,
            nodeId = nodeId,
            propertyPath = "m_LocalPosition",
            oldValue = "0,0,0",
            newValue = "2,3,4",
            revision = peer.Revision,
        });

        var moveAck = await peer.NextAsync(m => m.type == MessageTypes.Ack && m.opId == moveId);
        Check("server accepted the move", moveAck is { accepted: true }, "code=" + (moveAck?.code ?? "(none)"));

        var sawCube = await WaitForFile(Path.Combine(dir, "v03-host-saw.json"), 90);
        Check("unity applied the new object", sawCube, "v03-host-saw.json " + (sawCube ? "written" : "missing"));

        // Component add/remove. A guest could not touch components at all in v0.3: the asset manifest
        // only says which assets are needed, and nothing watched a component list for changes. The host
        // notices these through the component signature sweep, so this round also tests that sweep.
        const string ComponentType = "UnityEngine.AudioSource";

        var addComponentId = Guid.NewGuid().ToString("N");
        await peer.SendAsync(new WireMessage
        {
            type = MessageTypes.Op,
            protocolVersion = ProtocolVersion.Current,
            opId = addComponentId,
            opKind = OpKinds.AddComponent,
            nodeId = nodeId,
            propertyPath = "component",
            oldValue = "",
            newValue = ComponentType,
            componentType = ComponentType,
            revision = peer.Revision,
        });

        var addComponentAck = await peer.NextAsync(m => m.type == MessageTypes.Ack && m.opId == addComponentId);
        Check("server accepted add_component", addComponentAck is { accepted: true },
            "code=" + (addComponentAck?.code ?? "(none)"));

        var sawComponent = await WaitForFile(Path.Combine(dir, "v03-host-comp.json"), 90);
        Check("unity added the component", sawComponent,
            "v03-host-comp.json " + (sawComponent ? "written" : "missing"));

        var removeComponentId = Guid.NewGuid().ToString("N");
        await peer.SendAsync(new WireMessage
        {
            type = MessageTypes.Op,
            protocolVersion = ProtocolVersion.Current,
            opId = removeComponentId,
            opKind = OpKinds.RemoveComponent,
            nodeId = nodeId,
            propertyPath = "",
            oldValue = ComponentType,
            newValue = "",
            componentType = ComponentType,
            revision = peer.Revision,
        });

        var removeComponentAck = await peer.NextAsync(m => m.type == MessageTypes.Ack && m.opId == removeComponentId);
        Check("server accepted remove_component", removeComponentAck is { accepted: true },
            "code=" + (removeComponentAck?.code ?? "(none)"));

        var sawComponentGone = await WaitForFile(Path.Combine(dir, "v03-host-comp-removed.json"), 90);
        Check("unity removed the component", sawComponentGone,
            "v03-host-comp-removed.json " + (sawComponentGone ? "written" : "missing"));

        var removeId = Guid.NewGuid().ToString("N");
        await peer.SendAsync(new WireMessage
        {
            type = MessageTypes.Op,
            protocolVersion = ProtocolVersion.Current,
            opId = removeId,
            opKind = OpKinds.RemoveNode,
            nodeId = nodeId,
            oldValue = "{\"name\":\"Guest Cube\"}",
            newValue = "",
            revision = peer.Revision,
        });

        var removeAck = await peer.NextAsync(m => m.type == MessageTypes.Ack && m.opId == removeId);
        Check("server accepted remove_node", removeAck is { accepted: true }, "code=" + (removeAck?.code ?? "(none)"));

        var sawRemoval = await WaitForFile(Path.Combine(dir, "v03-host-removed.json"), 90);
        Check("unity removed the object", sawRemoval, "v03-host-removed.json " + (sawRemoval ? "written" : "missing"));

        await WriteShared(Path.Combine(dir, "v03-guest-result.json"),
            JsonSerializer.Serialize(new { ok = failures == 0, failed = failures, nodeId }));

        await peer.CloseAsync();
        Console.WriteLine(failures == 0 ? "[guest] PASS" : "[guest] FAIL (" + failures + ")");
        return failures == 0 ? 0 : 1;
    }

    static async Task<bool> WaitForFile(string path, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    static async Task<string> ReadShared(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(path);
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
        }

        return await File.ReadAllTextAsync(path);
    }

    static Task WriteShared(string path, string content)
    {
        File.WriteAllText(path, content);
        return Task.CompletedTask;
    }
}
