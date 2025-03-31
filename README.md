# SendIt - P2P File Transfer App

**SendIt** is a C# WPF app for peer-to-peer file transfers over UDP, supporting NAT traversal via UDP hole punching. It streams large files efficiently and uses a rendezvous server to connect peers.

## Features
- P2P file transfer with NAT traversal.
- Streams large files (e.g., 1GB+ videos) in 8KB chunks.
- Reliable UDP with ACKs and retries.
- Simple WPF UI for connecting and sending files.
- Lightweight rendezvous server for peer coordination.

## Prerequisites
- .NET 8.0 SDK
- Visual Studio 2022 (with WPF workload)
- Two devices/networks for testing
- Google Cloud Free Tier account
