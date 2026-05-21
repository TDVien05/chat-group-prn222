# AGENTS.md

## Project Type

This repository contains a C#/.NET WPF desktop chat client and a console chat server.

Architecture: 3-layer architecture.

- Presentation Layer: WPF UI
- Business Layer: application logic, validation, services
- Data/Infrastructure Layer: networking, persistence, DTOs, repositories

## Main Rules

- Use C# and .NET.
- Use WPF with MVVM.
- Do not put business logic in code-behind.
- Code-behind is only allowed for view-specific UI wiring.
- Use async/await for network and IO operations.
- Never block UI thread.
- Prefer dependency injection.
- Keep classes small and focused.
- Use clear English names for classes, methods, properties.
- Do not create God classes.
- Do not hardcode connection settings inside UI classes.

## Solution Structure

Preferred solution layout:

```txt
ChatApp.sln

/src
  /ChatClient.Wpf
    /Views
    /ViewModels
    /Commands
    /Resources
    App.xaml
    MainWindow.xaml

  /ChatClient.Business
    /Interfaces
    /Services
    /Models
    /Validation

  /ChatClient.Infrastructure
    /Networking
    /Repositories
    /Dtos
    /Config

  /ChatServer.Console
    /Services
    /Networking
    /Models
    Program.cs

/tests
  /ChatClient.Tests
  /ChatServer.Tests