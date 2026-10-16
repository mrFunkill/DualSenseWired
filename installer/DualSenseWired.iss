; DualSense Wired for Windows: installer (Inno Setup 6).
; Build: publish the app to ..\publish (see build.ps1), put the dependency installers into deps\,
; then compile this script with ISCC.exe.

#define AppName "DualSense Wired"
#define AppVersion "0.1.0"
#define AppExe "DualSenseWired.exe"
#define UsbIpSetup "USBip-0.9.8.1-x64.exe"
#define HidHideSetup "HidHide_1.5.230_x64.exe"

[Setup]
AppId={{FF7BDDFB-D6AC-4302-827F-C5F03C0ADDB9}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=mrFunkill
AppPublisherURL=https://github.com/mrFunkill/decky-dualsense-wired
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=output
OutputBaseFilename=DualSenseWired-{#AppVersion}-setup
SetupIconFile=..\DualSenseWired\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
LicenseFile=license.txt

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.Autostart=Start with Windows (in the tray)
ru.Autostart=Запускать вместе с Windows (в трее)
en.InstallUsbIp=Installing usbip-win2 (virtual USB device driver)...
ru.InstallUsbIp=Установка usbip-win2 (драйвер виртуального USB-устройства)...
en.InstallHidHide=Installing HidHide (hides the Bluetooth controller from games)...
ru.InstallHidHide=Установка HidHide (скрывает Bluetooth-геймпад от игр)...
en.Dependencies=Also installs, if missing: usbip-win2 0.9.8.1 (BSD-2-Clause) and HidHide 1.5 (MIT). A restart may be needed after the driver installation.
ru.Dependencies=Также будут установлены, если их нет: usbip-win2 0.9.8.1 (BSD-2-Clause) и HidHide 1.5 (MIT). После установки драйверов может понадобиться перезагрузка.
en.Launch=Start {#AppName}
ru.Launch=Запустить {#AppName}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "{cm:Autostart}"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "license.txt"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "deps\{#UsbIpSetup}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedUsbIp
Source: "deps\{#HidHideSetup}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedHidHide

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\{#UsbIpSetup}"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"; StatusMsg: "{cm:InstallUsbIp}"; Check: NeedUsbIp; AfterInstall: DriverInstalled
Filename: "{tmp}\{#HidHideSetup}"; Parameters: "/quiet /norestart"; StatusMsg: "{cm:InstallHidHide}"; Check: NeedHidHide; AfterInstall: DriverInstalled
Filename: "{app}\{#AppExe}"; Parameters: "--set-autostart on"; Flags: runhidden; Tasks: autostart
Filename: "{app}\{#AppExe}"; Parameters: "--set-autostart off"; Flags: runhidden; Tasks: not autostart
Filename: "{app}\{#AppExe}"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent; Check: not RestartNeeded

[UninstallRun]
; Stop it cleanly (unplugs the virtual controller, gives the real one back to Steam), drop autostart.
Filename: "{app}\{#AppExe}"; Parameters: "--stop"; Flags: runhidden; RunOnceId: "Stop"
Filename: "{app}\{#AppExe}"; Parameters: "--set-autostart off"; Flags: runhidden; RunOnceId: "Autostart"

[Code]
var
  DriversInstalled: Boolean;

function UsbIpExe: String;
begin
  Result := ExpandConstant('{commonpf64}\USBip\usbip.exe');
end;

{ usbip-win2 0.9.7.8 can corrupt kernel memory (BSOD, see its release notes): replace anything older than 0.9.8.0. }
function NeedUsbIp: Boolean;
var
  MS, LS: Cardinal;
begin
  Result := not FileExists(UsbIpExe);
  { MS = major.minor, LS = build.revision: 0.9.8.0 is MS $00000009, LS $00080000. }
  if not Result then
    Result := not GetVersionNumbers(UsbIpExe, MS, LS) or (MS < $00000009) or ((MS = $00000009) and (LS < $00080000));
end;

function NeedHidHide: Boolean;
begin
  Result := not FileExists(ExpandConstant('{commonpf64}\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe'))
        and not FileExists(ExpandConstant('{commonpf64}\Nefarius Software Solutions e.U\HidHide\x64\HidHideCLI.exe'));
end;

procedure DriverInstalled;
begin
  DriversInstalled := True;
end;

function RestartNeeded: Boolean;
begin
  Result := DriversInstalled;
end;

function NeedRestart: Boolean;
begin
  Result := DriversInstalled;
end;

{ An update: stop the running copy first so its files can be replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
    Exec(Exe, '--stop', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

procedure InitializeWizard;
begin
  WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption + #13#10#13#10 + CustomMessage('Dependencies');
end;
