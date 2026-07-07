// Usings globais para todo o projeto de testes. O SDK (ImplicitUsings=enable,
// definido em Directory.Build.props) já injeta System, System.Collections.Generic,
// System.IO, System.Linq e System.Threading.Tasks — aqui completamos com os
// frameworks de teste e os namespaces do projeto NotebookCheck sob teste.
global using System.Globalization;
global using FluentAssertions;
global using FsCheck.Xunit;
global using Xunit;
global using NotebookCheck.Application.Api;
global using NotebookCheck.Domain.Enums;
global using NotebookCheck.Domain.Models;
global using NotebookCheck.Domain.Rules;
global using NotebookCheck.Domain.Validation;
global using NotebookCheck.Infrastructure.Api;
