# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this project.

## Project Overview

**RIoT2.Matter.ControlBridge** is a .NET 9 **class library** that wraps a Matter **Control Bridge**
(device type `0x0840`) and optional **Aggregator** (device type `0x000E`) into a single,
controller-facing API. It provides Matter connectivity for 3rd-party controllers by:

- Composing and hosting the Control Bridge device.
- Optionally exposing non-Matter devices as bridged endpoints through an Aggregator.
- Generating the onboarding **QR code (`MT:` string)** and **11-digit manual pairing code** a
  commissioner scans.
- Driving the bridge's bound targets by opening operational (CASE) sessions and routing commands to them.

The library builds on [`RIoT2.Matter`](../README.md), which supplies the raw building
blocks (device composition, hosting, Secure Channel, Interaction Model, DNS-SD).

## Target Framework

- **.NET 9** (`net9.0`).
- `ImplicitUsings` and `Nullable` are **enabled** — assume a nullable-aware context and omit common
  `using` directives where the SDK provides them implicitly.

## Key Concepts

- **Matter Control Bridge (`0x0840`)**: A controller endpoint that binds to and drives On/Off, Level
  Control, and Color Control on other nodes. It hosts Identify + Groups + Binding as servers and
  declares On/Off, Level Control, and Color Control as clients.
- **Matter Aggregator (`0x000E`)**: Optional role enabled by `ControlBridgeSettings.AggregatorEndpoint`.
  It exposes bridged (`0x0013`) endpoints with Bridged Device Basic Information and application
  clusters, driven by an `IBridgedDeviceAdapter`.
- **Commissioning**: A commissioner adds the bridge to a fabric using the onboarding codes, then writes
  the bridge's **Binding** list over CASE.
- **Binding-driven sessions**: When the Binding list changes, the library automatically opens CASE
  sessions to the bound peers. Removing a fabric purges its bindings and tears down its sessions.
- **Single provisioning bundle**: The scanned passcode and the on-device SPAKE2+ verifier come from the
  same provisioning bundle, so they can never diverge.

## Architecture

## Key Types

- `ControlBridgeService` — The controller-facing façade. Create it from `ControlBridgeSettings`, start
  it, present the onboarding codes, then invoke bound targets. Implements `IAsyncDisposable`.
- `ControlBridgeSettings` — Device identity, attestation, discriminator, and discovery configuration.
- `ControlBridgeOnboarding` — The onboarding artifacts (QR string, manual pairing code, discriminator,
  passcode).
- `RIoT2.Matter.Clusters.ControlBridge` — The composed node (root endpoint + controller endpoint).
- `AggregatorEndpoint`, `BridgedDevice`, `IBridgedDeviceAdapter` — Optional bridged-device runtime.
- `IOperationalPeerResolver` — Resolves each unicast peer's operational IP endpoint for outbound CASE.
  Supply a static map or a resolver backed by operational DNS-SD; the default resolver reports every
  peer as unresolvable.

## Usage Pattern

```
await using var service = ControlBridgeService.Create(settings, resolver); await service.StartAsync(); Console.WriteLine(service.Onboarding.QrCode);
// A commissioner writes the bridge's Binding list; connections to bound peers follow automatically. await service.InvokeAsync(new OperationalPeer(fabricIndex, peerNodeId), new EndpointId(1), OnOffCluster.ClusterId, new CommandId(0x02)); // Toggle
```

## Runtime Requirements

- An **IPv6-capable** network interface. Matter is IPv6-centric; the operational UDP port is **5540**.
- Device **attestation credentials** (DAC/PAI/CD + DAC signer).
- Production hosts should persist `service.Device.Commissioning.Manager` with `FileFabricPersistence`
  or equivalent encrypted storage. `settings.Provisioning` only pins the setup passcode/verifier; it
  does not persist commissioned fabric credentials.

## Dependencies

- References only `RIoT2.Matter` (`..\RIoT2.Matter\RIoT2.Matter.csproj`).
- QR-code **rendering** (ASCII or image) is intentionally left to the consumer — the library returns the
  `MT:` onboarding string; render it with a library such as `QRCoder`.

## Conventions

- Follow the existing coding style: expression-bodied members where concise, XML doc comments on public
  APIs, and file-scoped namespaces.
- Public types are `sealed` unless designed for extension.
- Prefer explicit ownership and disposal ordering (dispose connection managers before the host, and the
  host before the bridge) as documented in the type remarks.

## Related

- [`RIoT2.Matter`](../README.md) — the underlying Matter stack.
- Matter Device Library Specification (Control Bridge `0x0840`) and Matter Core Specification, section 9.6.
