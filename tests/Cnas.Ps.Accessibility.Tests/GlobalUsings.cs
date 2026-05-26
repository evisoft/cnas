// Shared usings for every test in this project. Mirrors the pattern established
// by Cnas.Ps.E2E.Tests so xUnit + FluentAssertions live at the assembly level
// rather than being re-imported in every file.
global using Xunit;
global using FluentAssertions;
global using Microsoft.Extensions.DependencyInjection;
