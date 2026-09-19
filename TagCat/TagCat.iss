; TagCat - Inno Setup script.
;
; Produces a normal Windows installer (wizard, Start Menu shortcut, uninstaller in
; "Add or Remove Programs") for a small, framework-dependent build - it does NOT bundle
; the .NET Runtime or VLC, and instead detects whether the machine already has them and
; downloads whichever is missing during setup.
;
; Downloading is done with Inno Setup's own built-in support (CreateDownloadPage /
; DownloadTemporaryFile, added in version 6.1) rather than a third-party plugin - an
; earlier version of this script used the "Inno Download Plugin", which turned out to be
; a stale 2015 project scattered across several unofficial GitHub mirrors with no
; reliable download link, and which Inno Setup's own maintainers now consider
; unnecessary since this was added. One fewer thing to install, one fewer broken link.
;
; BEFORE COMPILING THIS:
;   1. Run MAKE-INSTALLER-PACKAGE.bat first. It publishes TagCat without the .NET
;      runtime or LibVLC bundled in, into InstallerPackage\app - this script packages
;      exactly that folder, so it has to exist first.
;
; TO COMPILE THIS:
;   1. Install Inno Setup 6.1 or later (free): https://jrsoftware.org/isinfo.php
;      That's the only tool needed - nothing else to download or install.
;   2. Open this file in Inno Setup and press Compile (or Build > Compile). The finished
;      installer appears in an "Output" folder next to this script.
;
; NOT YET TESTED END TO END. I don't have a Windows machine to run the Inno Setup
; compiler or the finished installer on, so this has been written carefully, and its
; download approach checked against Inno Setup's own documentation and example scripts,
; but not verified against a real install. Try it on a clean Windows install or a VM
; first.

#define MyAppName "TagCat"
#define MyAppVersion "0.61"
; The Duplicate Finder carries its own version. Both go in the installer's filename, in the
; same "v0.<TagCat>.<DF>" form the release zip uses, so a Setup.exe sitting in a downloads
; folder says exactly which pair of versions it contains.
#define MyDedupeVersion "19"
#define MyAppExeName "TagCat.exe"
#define MyAppPublisher "TagCat"

[Setup]
; Fresh GUID for the TagCat name - reusing the old Media Tagger one would make Windows
; treat this as an upgrade of that app, muddling its Add/Remove Programs entry.
AppId={{7C4E9A02-3B18-4D6F-9E5A-1F8B2C6D40A7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
; No Start Menu subfolder - the shortcut goes straight into the Start Menu root, so
; there's no group folder concept and nothing for this page to ask about.
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=TagCat-Setup v{#MyAppVersion}.{#MyDedupeVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

; Everything MAKE-INSTALLER-PACKAGE.bat published (already has the .NET runtime and
; LibVLC stripped out of it) gets installed as-is. The .NET Runtime installer and
; LibVLC's zip are NOT listed here - they are downloaded straight to {tmp} in [Code]
; below and never become part of the installed app's own file list.
[Files]
Source: "InstallerPackage\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Embedded in the installer and extracted to {tmp} only when ExtractTemporaryFile is
; called below - dontcopy means it never becomes part of the installed app itself. Kept
; as its own file rather than an inline PowerShell command in [Run]: PowerShell's { }
; script-block syntax collides with Inno Setup's own {constant} syntax when written
; inline, which a single stray brace inside a quoted [Run] string is enough to break.
Source: "ExtractLibVlc.ps1"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Only actually runs if the .NET download happened in [Code] and succeeded - Check:
; skips this step entirely otherwise, which is also what happens if the download failed
; or wasn't needed, since the temp file then never exists either.
Filename: "{tmp}\dotnet-desktop-runtime.exe"; Parameters: "/install /quiet /norestart"; \
    StatusMsg: "Installing the .NET 9 Desktop Runtime..."; \
    Check: FileExists(ExpandConstant('{tmp}\dotnet-desktop-runtime.exe')); \
    Flags: waituntilterminated

; LibVLC arrives as a NuGet package, which is really just a zip file - Inno Setup has no
; built-in step for "unzip this and copy the right subfolder," so this hands that one
; narrow task to the PowerShell script extracted above rather than the whole installer.
; Everything else here - the wizard, the detection logic, the download itself, the
; Start Menu entry, the uninstaller - is Inno Setup.
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\ExtractLibVlc.ps1"" -ZipPath ""{tmp}\libvlc.nupkg.zip"" -AppDir ""{app}"""; \
    StatusMsg: "Installing VLC..."; \
    Check: FileExists(ExpandConstant('{tmp}\libvlc.nupkg.zip')); \
    Flags: waituntilterminated runhidden

Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: postinstall nowait skipifsilent

[Code]
var
  DotNetNeeded, VlcNeeded: Boolean;
  DownloadPage: TDownloadWizardPage;

// Checked by folder presence rather than shelling out to "dotnet --list-runtimes" and
// parsing its output - simpler and more reliable from Pascal script code.
function IsDotNet9DesktopInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\9.*'), FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function IsVlcInstalled: Boolean;
begin
  // Explicit 64-bit and 32-bit paths, checked regardless of which mode this installer
  // itself is running in - VLC could have been installed as either, independently.
  Result := FileExists(ExpandConstant('{commonpf64}\VideoLAN\VLC\libvlc.dll')) or
            FileExists(ExpandConstant('{commonpf32}\VideoLAN\VLC\libvlc.dll'));
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if Progress = ProgressMax then
    Log(Format('Downloaded: %s', [FileName]));
  Result := True;
end;

procedure InitializeWizard;
begin
  DotNetNeeded := not IsDotNet9DesktopInstalled;
  VlcNeeded := not IsVlcInstalled;

  // Writes the file dontcopy'd above out to {tmp}, where the [Run] step further down
  // expects to find it. Harmless to do even if VLC turns out not to be needed.
  ExtractTemporaryFile('ExtractLibVlc.ps1');

  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Msg: String;
begin
  Result := True;

  if (CurPageID = wpReady) and (DotNetNeeded or VlcNeeded) then
  begin
    if DotNetNeeded and VlcNeeded then
      Msg := 'TagCat needs the .NET 9 Desktop Runtime and VLC to run, and neither ' +
             'was found on this computer.'
    else if DotNetNeeded then
      Msg := 'TagCat needs the .NET 9 Desktop Runtime to run, and it was not found ' +
             'on this computer.'
    else
      Msg := 'TagCat needs VLC for video and audio preview, and it was not found ' +
             'on this computer.';

    if MsgBox(Msg + #13#10#13#10 +
        'Setup will download it now. This may take a few minutes depending on your ' +
        'connection.' + #13#10#13#10 +
        'Continue? Choosing No will still install TagCat, but you''ll need to ' +
        'install this yourself afterwards.',
        mbConfirmation, MB_YESNO) <> IDYES then
    begin
      // Declining the download is not the same as cancelling setup - the app files
      // still get installed, just without this. TagCat's own "couldn't find VLC"
      // message on first launch covers the rest.
      DotNetNeeded := False;
      VlcNeeded := False;
      Exit;
    end;

    DownloadPage.Clear;

    if DotNetNeeded then
      DownloadPage.Add('https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe',
        'dotnet-desktop-runtime.exe', '');

    if VlcNeeded then
      DownloadPage.Add('https://api.nuget.org/v3-flatcontainer/videolan.libvlc.windows/3.0.23.1/videolan.libvlc.windows.3.0.23.1.nupkg',
        'libvlc.nupkg.zip', '');

    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        // Not fatal to the install itself - the [Run] steps above only fire if their
        // file actually exists, so setup still finishes either way; TagCat just
        // shows its own "couldn't find VLC" message on first launch instead, with
        // instructions for fixing it by hand.
        if DownloadPage.AbortedByUser then
          Result := False
        else
          SuppressibleMsgBox('Could not download one of the files this needs. Setup will ' +
            'continue, but video/audio preview may not work until VLC is installed by hand ' +
            '(videolan.org) or the .NET 9 Desktop Runtime is (dotnet.microsoft.com).',
            mbError, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;
