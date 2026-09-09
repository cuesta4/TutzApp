using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace TutzApp.Tests;

public sealed class PackagingManifestTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    private static readonly XNamespace Uap10 =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";

    [Fact]
    public void SparsePackageManifest_UsesExternalLocationWin32Contract()
    {
        XDocument document = XDocument.Load(FindProjectFile("build", "ContextMenu", "AppxManifest.xml"));
        XElement package = Assert.IsType<XElement>(document.Root);
        XElement identity = Assert.Single(package.Elements(Foundation + "Identity"));
        XElement application = Assert.Single(
            package.Element(Foundation + "Applications")!.Elements(Foundation + "Application"));

        Assert.Equal("neutral", (string?)identity.Attribute("ProcessorArchitecture"));
        Assert.Equal("mediumIL", (string?)application.Attribute(Uap10 + "TrustLevel"));
        Assert.Equal("win32App", (string?)application.Attribute(Uap10 + "RuntimeBehavior"));
        Assert.Null(application.Attribute("EntryPoint"));

        XElement allowExternalContent = Assert.Single(
            package.Element(Foundation + "Properties")!.Elements(Uap10 + "AllowExternalContent"));
        Assert.Equal(
            "true",
            allowExternalContent.Value.Trim().ToLowerInvariant());
    }

    [Fact]
    public void MainExecutable_RemainsUnpackagedSoContextMenuDeploymentCannotTerminateIt()
    {
        XDocument packageDocument = XDocument.Load(FindProjectFile("build", "ContextMenu", "AppxManifest.xml"));
        XElement package = Assert.IsType<XElement>(packageDocument.Root);
        XElement application = Assert.Single(
            package.Element(Foundation + "Applications")!.Elements(Foundation + "Application"));

        Assert.Equal("TutzApp.exe", (string?)application.Attribute("Executable"));

        XDocument executableManifest = XDocument.Load(FindProjectFile("app.manifest"));
        XNamespace msix = "urn:schemas-microsoft-com:msix.v1";
        Assert.Empty(executableManifest.Descendants(msix + "msix"));
    }

    [Fact]
    public void SparsePackagePostBuildUnpack_DisablesSemanticValidation()
    {
        string script = File.ReadAllText(
            FindProjectFile("build", "ContextMenu", "build-modern-context-menu.ps1"));

        Assert.Contains(
            "@('unpack', '/o', '/nv', '/p', $packagePath, '/d', $packageVerifyDirectory)",
            script);
    }


    [Fact]
    public void PackageInstaller_TrustsCertificateInLocalMachineTrustedPeople()
    {
        string serviceSource = File.ReadAllText(
            FindProjectFile("src", "Features", "ContextMenu", "Services", "SystemControlService.ContextMenu.cs"));

        Assert.Contains("StoreLocation.LocalMachine", serviceSource);
        Assert.Contains("StoreName.TrustedPeople", serviceSource);
        Assert.Contains(@"Cert:\\LocalMachine\\TrustedPeople", serviceSource);
        Assert.Contains("Verb = \"runas\"", serviceSource);
        Assert.DoesNotContain(
            @"Import-Certificate -FilePath $certificatePath -CertStoreLocation 'Cert:\\CurrentUser\\TrustedPeople'",
            serviceSource);
    }


    [Fact]
    public void PackageInstaller_DoesNotRunCurrentUserCertificateCleanupInsideCriticalPowerShell()
    {
        string serviceSource = File.ReadAllText(
            FindProjectFile("src", "Features", "ContextMenu", "Services", "SystemControlService.ContextMenu.cs"));

        Assert.DoesNotContain("$currentUserTrustedPath =", serviceSource);
        Assert.DoesNotContain("$trustedPath = 'Cert:\\CurrentUser\\TrustedPeople\\'", serviceSource);
        Assert.Contains("RemoveCertificateFromCurrentUserTrustedPeople(thumbprint);", serviceSource);
        Assert.Contains("Cleanup of the obsolete per-user trust entry is best effort only.", serviceSource);
    }

    [Fact]
    public void ExplorerCommand_ReadsPerUserVisibilityForBothSubcommands()
    {
        string nativeSource = File.ReadAllText(
            FindProjectFile("src", "Features", "ContextMenu", "Native", "ExplorerCommand.cpp"));

        Assert.Contains(@"Software\\TutzApp\\ShellIntegration", nativeSource);
        Assert.Contains("ShowNormal", nativeSource);
        Assert.Contains("ShowElevated", nativeSource);
        Assert.Contains("RegGetValueW", nativeSource);
        Assert.Contains("ECS_HIDDEN", nativeSource);
        Assert.Contains("IsCommandEnabled(CommandKind::Normal)", nativeSource);
        Assert.Contains("IsCommandEnabled(CommandKind::Elevated)", nativeSource);
        Assert.Contains("-ladvapi32", File.ReadAllText(
            FindProjectFile("build", "ContextMenu", "build-modern-context-menu.ps1")));
    }


    [Fact]
    public void ExplorerCommandComServer_UsesMicrosoftStaSurrogateContract()
    {
        XDocument document = XDocument.Load(FindProjectFile("build", "ContextMenu", "AppxManifest.xml"));
        XNamespace com = "http://schemas.microsoft.com/appx/manifest/com/windows10";
        XElement surrogate = Assert.Single(document.Descendants(com + "SurrogateServer"));
        XElement classElement = Assert.Single(document.Descendants(com + "Class"));

        Assert.Equal("F13BF4B5-9064-4971-B73E-1D5EC8F2EAA5",
            ((string?)surrogate.Attribute("AppId"))?.ToUpperInvariant());
        Assert.Equal(
            ((string?)surrogate.Attribute("AppId"))?.ToUpperInvariant(),
            ((string?)classElement.Attribute("Id"))?.ToUpperInvariant());
        Assert.Equal("STA", (string?)classElement.Attribute("ThreadingModel"));

        string nativeSource = File.ReadAllText(FindProjectFile(
            "src", "Features", "ContextMenu", "Native", "ExplorerCommand.cpp"));
        Assert.DoesNotContain("IID_IAgileObject", nativeSource);
        Assert.DoesNotContain("SRWLOCK stateLock_", nativeSource);
        Assert.DoesNotContain("IObjectWithSite", nativeSource);
        Assert.Contains("GetParentIconReference", nativeSource);
        Assert.Contains(@"\TutzApp.exe,0", nativeSource);
        Assert.Contains("modern-context-menu-native.log", nativeSource);
        Assert.Contains("InterlockedIncrement", nativeSource);
    }

    [Fact]
    public void SparsePackageVersion_IsAtLeastContextMenuCommandManagerVersion()
    {
        XDocument document = XDocument.Load(FindProjectFile("build", "ContextMenu", "AppxManifest.xml"));
        XElement package = Assert.IsType<XElement>(document.Root);
        XElement identity = Assert.Single(package.Elements(Foundation + "Identity"));

        Version version = Version.Parse(Assert.IsType<string>((string?)identity.Attribute("Version")));
        Assert.True(version.CompareTo(new Version(1, 0, 0, 7)) >= 0);
    }

    private static string FindProjectFile(params string[] relativeParts)
    {
        IEnumerable<string> startingDirectories = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (string startingDirectory in startingDirectories)
        {
            DirectoryInfo? directory = new(startingDirectory);
            while (directory is not null)
            {
                string candidate = directory.FullName;
                foreach (string part in relativeParts)
                {
                    candidate = Path.Combine(candidate, part);
                }
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException(
            $"Could not locate project file: {Path.Combine(relativeParts)}");
    }
}
