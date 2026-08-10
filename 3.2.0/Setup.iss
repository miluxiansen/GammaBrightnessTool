; Gamma Brightness Tool Installer Script

#define MyAppName "Gamma Brightness Tool"
#define MyAppVersion "3.2.0"
#define MyAppPublisher "Gamma Brightness"
#define MyAppExeName "GammaBrightnessTool.exe"
#define MyAppSource "GreenVersion\GammaBrightnessTool_3.2.0_20260810_2132.exe"

[Setup]
AppId={{B8E3A5C1-2D4F-4A6B-9C8E-1F3A5B7D9E2C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={code:GetDefaultDir}
UsePreviousAppDir=no
DefaultGroupName={#MyAppName}
OutputDir=Installer
OutputBaseFilename=GammaBrightnessTool_Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
SetupIconFile=Resources\APP.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
ShowLanguageDialog=no
DisableWelcomePage=yes
DisableDirPage=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.AutoStart=开机自动启动 / Start with Windows
chinesesimplified.LaunchProgram=运行 %1 (Run)
english.AutoStart=Start with Windows / 开机自动启动
english.LaunchProgram=Launch %1

[Messages]
chinesesimplified.WizardSelectDir=选择目标位置 / Select Destination
chinesesimplified.SelectDirLabel3=安装程序将安装 {#MyAppName} 到下列文件夹中。 / Setup will install {#MyAppName} into the following folder.
chinesesimplified.SelectDirDesc=您想将 {#MyAppName} 安装在哪里？ / Where should {#MyAppName} be installed?
chinesesimplified.SelectDirBrowseLabel=要继续，单击"下一步"。要选择其他文件夹，单击"浏览"。 / To continue, click Next. If you want to select a different folder, click Browse.
chinesesimplified.WizardSelectProgramGroup=选择"开始"菜单文件夹 / Select Start Menu Folder
chinesesimplified.SelectStartMenuFolderLabel3=安装程序将在下列"开始"菜单文件夹中创建快捷方式。 / Setup will create shortcuts in the following Start Menu folder.
chinesesimplified.SelectStartMenuFolderDesc=安装程序将在下列"开始"菜单文件夹中创建快捷方式。 / Setup will create shortcuts in the following Start Menu folder.
chinesesimplified.WizardSelectTasks=选择附加任务 / Select Additional Tasks
chinesesimplified.SelectTasksDesc=请选择安装时要执行的附加任务。 / Select additional tasks to perform during installation.
chinesesimplified.SelectTasksLabel2=选择您想要安装程序在安装 {#MyAppName} 时执行的附加任务，然后点击"下一步"。 / Select the additional tasks you want Setup to perform, then click Next.
chinesesimplified.WizardReady=准备安装 / Ready to Install
chinesesimplified.ReadyLabel1=安装程序准备就绪，现在可以开始安装 {#MyAppName}。 / Setup is now ready to begin installing {#MyAppName} on your computer.
chinesesimplified.ReadyLabel2a=单击"安装"继续，单击"上一步"查看或修改设置。 / Click Install to continue, or Back to review settings.
chinesesimplified.ReadyMemoTasks=附加任务 / Additional tasks:
chinesesimplified.WizardInstalling=正在安装 / Installing
chinesesimplified.FinishedHeadingLabel=完成 {#MyAppName} 安装向导 / Completing the {#MyAppName} Setup Wizard
chinesesimplified.FinishedLabel=安装程序已在你的电脑中安装 {#MyAppName}。 / Setup has finished installing {#MyAppName} on your computer.
chinesesimplified.ClickNext=单击"下一步"继续，或单击"取消"退出安装程序。 / Click Next to continue, or Cancel to exit Setup.
chinesesimplified.WizardPreparing=正在准备安装 / Preparing to Install

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式 / Create Desktop Icon"; Flags: unchecked
Name: "startup"; Description: "{cm:AutoStart}"; Flags: unchecked

[Files]
Source: "{#MyAppSource}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GammaBrightnessTool"; ValueData: "{app}\{#MyAppExeName} --silent"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function GetDefaultDir(Param: String): String;
begin
  // 优先使用 D 盘（用户习惯将软件装 D 盘）；D 盘不存在时回退到系统盘 Program Files (x86)
  if DirExists('D:\') then
    Result := 'D:\Program Files (x86)\{#MyAppName}'
  else
    Result := ExpandConstant('{pf32}\{#MyAppName}');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('taskkill', '/f /im GammaBrightnessTool.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  RegDeleteValue(HKCU, 'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify', 'IconStreams');
  RegDeleteValue(HKCU, 'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify', 'PastIconsStream');
  Result := '';
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('taskkill', '/f /im GammaBrightnessTool.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DelTree(ExpandConstant('{userappdata}\GammaBrightnessTool'), True, True, True);
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GammaBrightnessTool');
    RegDeleteValue(HKCU, 'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify', 'IconStreams');
    RegDeleteValue(HKCU, 'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify', 'PastIconsStream');
  end;
end;
