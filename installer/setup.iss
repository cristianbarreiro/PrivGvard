; =====================================================================
; PrivGvard - Inno Setup Script
; Professional Windows Desktop Installer. The installed app itself remains asInvoker.
; =====================================================================

#define MyAppName "PrivGvard"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "cdev Studio"
#define MyAppExeName "PrivGvard.exe"
#define MyAppId "{{8E0F7A12-BFB3-4FE8-B9A5-48FD50A15A9A}"
#define MyAppUninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{8E0F7A12-BFB3-4FE8-B9A5-48FD50A15A9A}_is1"
#define UninstallGateMutex "Global\PrivLock_UninstallGate"
#define HandoffArgumentPrefix "/PRIVLOCK_HANDOFF="
#define HandoffEventPrefix "Global\PrivLock_UninstallReady_"
#define OwnershipRegistryKey "SOFTWARE\PrivLock\PrivilegedOwnership\v2"
#define ProfileListRegistryKey "SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"
#define RecoveryRelativePath "AppData\Local\PrivGvard\Recovery\"
#define LegacyRecoveryRelativePath "AppData\Local\PrivLock\Recovery\"
#define ActiveMarkerFile "active-session-v1.marker"
#define JournalFile "privacy-session-v1.json"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UsePreviousAppDir=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputBaseFilename=PrivGvard-Setup-1.0.0
SetupIconFile=..\src\PrivLock.Desktop\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallFilesDir={app}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes

; Ensure running instances are safely closed before installing/updating/uninstalling
CloseApplications=no
CloseApplicationsFilter={#MyAppExeName},PrivLock.exe
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish_out\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: postinstall nowait skipifsilent shellexec runasoriginaluser

[Code]
const
  WAIT_OBJECT_0 = 0;
  WAIT_ABANDONED = $00000080;
  EVENT_MODIFY_STATE = $0002;
  ERROR_FILE_NOT_FOUND = 2;
  ERROR_PATH_NOT_FOUND = 3;
  INVALID_FILE_ATTRIBUTES = $FFFFFFFF;
  PRIVLOCK_ATTRIBUTE_DIRECTORY = $0010;
  PRIVLOCK_ATTRIBUTE_REPARSE_POINT = $0400;
  KEY_READ_64 = $20119;
  MaximumJournalBytes = 2097152;

var
  UninstallGateHandle: THandle;

function CreateMutexW(lpMutexAttributes: LongWord; bInitialOwner: Boolean;
  lpName: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function WaitForSingleObject(hHandle: THandle; dwMilliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function ReleaseMutex(hMutex: THandle): Boolean;
  external 'ReleaseMutex@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function OpenEventW(dwDesiredAccess: LongWord; bInheritHandle: Boolean;
  lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function GetFileAttributesW(lpFileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function GetLastError: LongWord;
  external 'GetLastError@kernel32.dll stdcall';
function ExpandEnvironmentStringsW(lpSrc: String; lpDst: String; nSize: LongWord): LongWord;
  external 'ExpandEnvironmentStringsW@kernel32.dll stdcall';
function RegOpenKeyExW(hKey: LongWord; lpSubKey: String; ulOptions: LongWord;
  samDesired: LongWord; var phkResult: THandle): LongWord;
  external 'RegOpenKeyExW@advapi32.dll stdcall';
function RegCloseKey(hKey: THandle): LongWord;
  external 'RegCloseKey@advapi32.dll stdcall';

function Quoted(const Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function StartsText(const Prefix, Value: String): Boolean;
begin
  Result := CompareText(Copy(Value, 1, Length(Prefix)), Prefix) = 0;
end;

function IsHexToken(const Value: String): Boolean;
var
  Index: Integer;
  Character: Char;
begin
  Result := Length(Value) = 32;
  if not Result then
    exit;

  for Index := 1 to Length(Value) do
  begin
    Character := Value[Index];
    if not (((Character >= '0') and (Character <= '9')) or
            ((Character >= 'a') and (Character <= 'f')) or
            ((Character >= 'A') and (Character <= 'F'))) then
    begin
      Result := False;
      exit;
    end;
  end;
end;

procedure SkipJsonWhitespace(const Value: String; var Position: Integer);
begin
  while Position <= Length(Value) do
  begin
    if Pos(Value[Position], ' ' + #9 + #10 + #13) = 0 then
      exit;
    Position := Position + 1;
  end;
end;

function ReadJsonString(const Value: String; var Position: Integer;
  IsPropertyName: Boolean; var Token: String): Boolean;
var
  Start: Integer;
  Index: Integer;
  Character: Char;
begin
  Result := False;
  if (Position > Length(Value)) or (Value[Position] <> '"') then
    exit;
  Start := Position;
  Position := Position + 1;
  while Position <= Length(Value) do
  begin
    Character := Value[Position];
    if Character < #32 then
      exit;
    Position := Position + 1;
    if Character = '"' then
    begin
      Token := Copy(Value, Start, Position - Start);
      Result := True;
      exit;
    end;
    if Character = '\' then
    begin
      // The writer uses plain ASCII property names. Reject escaped aliases so they cannot
      // hide duplicate ownership/status fields from this independent admission check.
      if IsPropertyName or (Position > Length(Value)) then
        exit;
      Character := Value[Position];
      Position := Position + 1;
      if Character = 'u' then
      begin
        for Index := 1 to 4 do
        begin
          if Position > Length(Value) then
            exit;
          if Pos(Value[Position], '0123456789abcdefABCDEF') = 0 then
            exit;
          Position := Position + 1;
        end;
      end
      else if Pos(Character, '"\/bfnrt') = 0 then
        exit;
    end;
  end;
end;

// Context 1 is the session object, 2 its resource array, and 3 a resource object.
// Parse the entire bounded JSON document, rejecting duplicates and nonterminal ownership.
function ReadJsonValue(const Value: String; var Position: Integer; Depth, Context: Integer;
  var Scalar: String; var ConflictCount: Integer): Boolean;
var
  Opening: Char;
  Closing: Char;
  Key: String;
  ChildScalar: String;
  SeenKeys: TStringList;
  ChildContext: Integer;
  RequiredFields: Integer;
  SessionStatus: String;
  RestoredFlag: String;
  ItemCount: Integer;
  Start: Integer;
begin
  Result := False;
  Scalar := '';
  if Depth > 64 then
    exit;
  SkipJsonWhitespace(Value, Position);
  if Position > Length(Value) then
    exit;
  Opening := Value[Position];
  if (((Context = 1) or (Context = 3)) and (Opening <> '{')) or
     ((Context = 2) and (Opening <> '[')) then
    exit;
  if (Opening = '{') or (Opening = '[') then
  begin
    if Opening = '{' then Closing := '}' else Closing := ']';
    Position := Position + 1;
    RequiredFields := 0;
    ItemCount := 0;
    SessionStatus := '';
    RestoredFlag := '';
    SeenKeys := TStringList.Create;
    try
      SkipJsonWhitespace(Value, Position);
      if Position > Length(Value) then exit;
      if Value[Position] <> Closing then
      begin
        while True do
        begin
          ChildContext := 0;
          Key := '';
          if Opening = '{' then
          begin
            if not ReadJsonString(Value, Position, True, Key) then exit;
            Key := Lowercase(Copy(Key, 2, Length(Key) - 2));
            if SeenKeys.IndexOf(Key) >= 0 then exit;
            SeenKeys.Add(Key);
            SkipJsonWhitespace(Value, Position);
            if Position > Length(Value) then exit;
            if Value[Position] <> ':' then exit;
            Position := Position + 1;
            if (Context = 1) and (Key = 'resources') then ChildContext := 2;
          end
          else if Context = 2 then ChildContext := 3;

          if not ReadJsonValue(Value, Position, Depth + 1, ChildContext,
            ChildScalar, ConflictCount) then exit;
          ItemCount := ItemCount + 1;
          if ItemCount > 1024 then exit;
          if Context = 1 then
          begin
            if Key = 'schemaversion' then
            begin
              if ChildScalar <> '1' then exit;
              RequiredFields := RequiredFields or 1;
            end
            else if Key = 'isactive' then
            begin
              if ChildScalar <> 'false' then exit;
              RequiredFields := RequiredFields or 2;
            end
            else if Key = 'status' then
            begin
              if (ChildScalar <> '2') and (ChildScalar <> '3') then exit;
              SessionStatus := ChildScalar;
              RequiredFields := RequiredFields or 4;
            end
            else if Key = 'wasrestored' then
            begin
              if (ChildScalar <> 'true') and (ChildScalar <> 'false') then exit;
              RestoredFlag := ChildScalar;
              RequiredFields := RequiredFields or 8;
            end
            else if Key = 'resources' then RequiredFields := RequiredFields or 16;
          end
          else if Context = 3 then
          begin
            if Key = 'journalstate' then
            begin
              // Unchanged, ApplyFailed, Restored, Conflict. Every other state needs recovery.
              if (ChildScalar <> '3') and (ChildScalar <> '4') and
                 (ChildScalar <> '6') and (ChildScalar <> '8') then exit;
              if ChildScalar = '8' then ConflictCount := ConflictCount + 1;
              RequiredFields := RequiredFields or 1;
            end
            else if Key = 'modifiedbyprivlock' then
            begin
              if ChildScalar <> 'false' then exit;
              RequiredFields := RequiredFields or 2;
            end
            else if Key = 'ownershipuncertain' then
            begin
              if ChildScalar <> 'false' then exit;
              RequiredFields := RequiredFields or 4;
            end
            else if Key = 'executionmaystillbeinflight' then
            begin
              if ChildScalar <> 'false' then exit;
              RequiredFields := RequiredFields or 8;
            end;
          end;
          SkipJsonWhitespace(Value, Position);
          if Position > Length(Value) then exit;
          if Value[Position] = Closing then break;
          if Value[Position] <> ',' then exit;
          Position := Position + 1;
          SkipJsonWhitespace(Value, Position);
        end;
      end;
      Position := Position + 1;
      if Context = 1 then
      begin
        if RequiredFields <> 31 then exit;
        if (SessionStatus = '2') and ((RestoredFlag <> 'true') or (ConflictCount <> 0)) then exit;
        if (SessionStatus = '3') and ((RestoredFlag <> 'false') or (ConflictCount = 0)) then exit;
      end
      else if (Context = 3) and (RequiredFields <> 15) then exit;
    finally
      SeenKeys.Free;
    end;
    Result := True;
    exit;
  end;
  if Opening = '"' then
  begin
    Result := ReadJsonString(Value, Position, False, Scalar);
    exit;
  end;
  Start := Position;
  while Position <= Length(Value) do
  begin
    if Pos(Value[Position], ',]} ' + #9 + #10 + #13) > 0 then break;
    Position := Position + 1;
  end;
  Scalar := Copy(Value, Start, Position - Start);
  if (Scalar = 'true') or (Scalar = 'false') or (Scalar = 'null') then
  begin
    Result := True;
    exit;
  end;
  // Journal numeric fields are integers. Reject noncanonical, fractional, and exponent forms.
  if Length(Scalar) = 0 then exit;
  Start := 1;
  if Scalar[Start] = '-' then Start := Start + 1;
  if Start > Length(Scalar) then exit;
  if (Scalar[Start] = '0') and (Start < Length(Scalar)) then exit;
  while Start <= Length(Scalar) do
  begin
    if Pos(Scalar[Start], '0123456789') = 0 then exit;
    Start := Start + 1;
  end;
  Result := True;
end;

// FileExists and DirExists deliberately hide access errors. Admission must distinguish a
// genuinely absent child from an inaccessible path, a malformed entry, or a redirected path.
function TryInspectPath(const Path: String; IsDirectory: Boolean; var Exists: Boolean): Boolean;
var
  Attributes: LongWord;
  ErrorCode: LongWord;
begin
  Result := False;
  Exists := False;
  Attributes := GetFileAttributesW(Path);
  if Attributes = INVALID_FILE_ATTRIBUTES then
  begin
    ErrorCode := GetLastError;
    Result := (ErrorCode = ERROR_FILE_NOT_FOUND) or (ErrorCode = ERROR_PATH_NOT_FOUND);
    if not Result then Log('PrivGvard cannot inspect recovery path: ' + Path);
    exit;
  end;
  Exists := True;
  Result := ((Attributes and PRIVLOCK_ATTRIBUTE_REPARSE_POINT) = 0) and
    (((Attributes and PRIVLOCK_ATTRIBUTE_DIRECTORY) <> 0) = IsDirectory);
  if not Result then Log('PrivGvard rejected a redirected or invalid recovery path: ' + Path);
end;

function JournalMayBeActive(const JournalPath: String): Boolean;
var
  Contents: AnsiString;
  Json: String;
  Scalar: String;
  Position: Integer;
  ConflictCount: Integer;
  Size: Int64;
  Exists: Boolean;
begin
  Result := True;
  if not TryInspectPath(JournalPath, False, Exists) then exit;
  if not Exists then
  begin
    Result := False;
    exit;
  end;
  if not FileSize64(JournalPath, Size) then exit;
  if (Size <= 0) or (Size > MaximumJournalBytes) then exit;
  if not LoadStringFromFile(JournalPath, Contents) then
  begin
    Log('PrivGvard uninstall cannot read recovery journal: ' + JournalPath);
    exit;
  end;
  if (Length(Contents) <= 0) or (Length(Contents) > MaximumJournalBytes) then exit;
  Json := String(Contents);
  Position := 1;
  ConflictCount := 0;
  if not ReadJsonValue(Json, Position, 1, 1, Scalar, ConflictCount) then exit;
  SkipJsonWhitespace(Json, Position);
  Result := Position <= Length(Json);
end;

function IsInteractiveUserSid(const Sid: String): Boolean;
begin
  Result := StartsText('S-1-5-21-', Sid) or StartsText('S-1-12-1-', Sid);
end;

function ProfilePathMayHaveActiveSessionInDir(const ProfilePath, RelativeDir: String): Boolean;
var
  RecoveryPath: String;
  PrimaryJournal: String;
  ParentPath: String;
  Exists: Boolean;
  Index: Integer;
begin
  Result := True;
  // The marker uses the conventional profile path even when LocalAppData is redirected.
  // Reject unknown/redirected ancestors before considering any missing child authoritative.
  RecoveryPath := AddBackslash(ProfilePath) + RelativeDir;
  for Index := 4 to Length(RecoveryPath) do
  begin
    if RecoveryPath[Index] = '\' then
    begin
      ParentPath := Copy(RecoveryPath, 1, Index - 1);
      if not TryInspectPath(ParentPath, True, Exists) then exit;
      if not Exists then
      begin
        // A missing profile is not proof that its offline or deleted recovery state is safe.
        if Length(ParentPath) <= Length(ProfilePath) then exit;
        Result := False;
        exit;
      end;
    end;
  end;
  if not TryInspectPath(RecoveryPath + '{#ActiveMarkerFile}', False, Exists) then exit;
  if Exists then exit;
  PrimaryJournal := RecoveryPath + '{#JournalFile}';
  // A completed commit durably updates both copies before clearing the marker.
  Result := JournalMayBeActive(PrimaryJournal) or JournalMayBeActive(PrimaryJournal + '.bak');
end;

function ProfileMayHaveActiveSession(const ProfilePath: String): Boolean;
begin
  Result := ProfilePathMayHaveActiveSessionInDir(ProfilePath, '{#RecoveryRelativePath}') or
            ProfilePathMayHaveActiveSessionInDir(ProfilePath, '{#LegacyRecoveryRelativePath}');
end;

function AnyProfileMayHaveActiveSession: Boolean;
var
  ProfileSids: TArrayOfString;
  ProfilePath: String;
  ExpandedPath: String;
  ExpandedLength: LongWord;
  Index: Integer;
begin
  Result := True;
  if not RegGetSubkeyNames(HKLM64, '{#ProfileListRegistryKey}', ProfileSids) then
  begin
    Log('PrivGvard uninstall cannot enumerate Windows profiles.');
    exit;
  end;

  for Index := 0 to GetArrayLength(ProfileSids) - 1 do
  begin
    if IsInteractiveUserSid(ProfileSids[Index]) then
    begin
      if not RegQueryStringValue(
          HKLM64,
          '{#ProfileListRegistryKey}\' + ProfileSids[Index],
          'ProfileImagePath',
          ProfilePath) then
      begin
        Log('PrivGvard uninstall cannot resolve profile ' + ProfileSids[Index] + '.');
        exit;
      end;

      SetLength(ExpandedPath, 32768);
      ExpandedLength := ExpandEnvironmentStringsW(ProfilePath, ExpandedPath, Length(ExpandedPath));
      if (ExpandedLength = 0) or (ExpandedLength > LongWord(Length(ExpandedPath))) then exit;
      SetLength(ExpandedPath, ExpandedLength - 1);
      ProfilePath := RemoveBackslashUnlessRoot(ExpandedPath);
      // ProfileImagePath must resolve to an absolute local path; remote/offline state is unknown.
      if (Length(ProfilePath) < 4) or (Copy(ProfilePath, 2, 2) <> ':\') or
         (Pos('%', ProfilePath) > 0) or (Pos('/', ProfilePath) > 0) or
         (Pos('\..', ProfilePath) > 0) or (Pos('\.', ProfilePath) > 0) then
      begin
        Log('PrivGvard uninstall rejected an unresolved Windows profile path.');
        exit;
      end;
      if ProfileMayHaveActiveSession(ProfilePath) then
      begin
        Log('PrivGvard uninstall found active recovery state for profile ' + ProfileSids[Index] + '.');
        exit;
      end;
    end;
  end;

  Result := False;
end;

function AnyPrivilegedOwnershipClaim: Boolean;
var
  ValueNames: TArrayOfString;
  SubkeyNames: TArrayOfString;
  KeyHandle: THandle;
  OpenResult: LongWord;
begin
  Result := True;
  OpenResult := RegOpenKeyExW(HKLM, '{#OwnershipRegistryKey}', 0, KEY_READ_64, KeyHandle);
  if OpenResult = ERROR_FILE_NOT_FOUND then
  begin
    Result := False;
    exit;
  end;
  if OpenResult <> 0 then
  begin
    Log('PrivGvard uninstall cannot open privileged ownership claims.');
    exit;
  end;
  if RegCloseKey(KeyHandle) <> 0 then exit;

  if not RegGetValueNames(HKLM64, '{#OwnershipRegistryKey}', ValueNames) or
     not RegGetSubkeyNames(HKLM64, '{#OwnershipRegistryKey}', SubkeyNames) then
  begin
    Log('PrivGvard uninstall cannot enumerate privileged ownership claims.');
    Result := True;
    exit;
  end;

  Result := (GetArrayLength(ValueNames) > 0) or (GetArrayLength(SubkeyNames) > 0);
end;

function TryAcquireUninstallGate: Boolean;
var
  WaitResult: LongWord;
begin
  Result := False;
  UninstallGateHandle := CreateMutexW(0, False, '{#UninstallGateMutex}');
  if UninstallGateHandle = 0 then
    exit;

  WaitResult := WaitForSingleObject(UninstallGateHandle, 0);
  Result := (WaitResult = WAIT_OBJECT_0) or (WaitResult = WAIT_ABANDONED);
  if not Result then
  begin
    CloseHandle(UninstallGateHandle);
    UninstallGateHandle := 0;
  end;
end;

procedure ReleaseUninstallGate;
begin
  if UninstallGateHandle <> 0 then
  begin
    ReleaseMutex(UninstallGateHandle);
    CloseHandle(UninstallGateHandle);
    UninstallGateHandle := 0;
  end;
end;

function SignalWrapperAdmission: Boolean;
var
  Index: Integer;
  Token: String;
  Candidate: String;
  EventHandle: THandle;
begin
  Token := '';
  for Index := 1 to ParamCount do
  begin
    Candidate := ParamStr(Index);
    if StartsText('{#HandoffArgumentPrefix}', Candidate) then
    begin
      if Token <> '' then
      begin
        Result := False;
        exit;
      end;
      Token := Copy(Candidate, Length('{#HandoffArgumentPrefix}') + 1, MaxInt);
    end;
  end;

  if Token = '' then
  begin
    Result := True;
    exit;
  end;
  if not IsHexToken(Token) then
  begin
    Result := False;
    exit;
  end;

  EventHandle := OpenEventW(EVENT_MODIFY_STATE, False, '{#HandoffEventPrefix}' + Token);
  if EventHandle = 0 then
  begin
    Result := False;
    exit;
  end;

  Result := SetEvent(EventHandle);
  CloseHandle(EventHandle);
end;

// Replace Inno's direct elevated command with the installed asInvoker binary. Recovery therefore
// runs under the interactive user's token before PrivLock starts the validated Inno executable.
procedure CurStepChanged(CurStep: TSetupStep);
var
  WrapperPath: String;
  UninstallerPath: String;
  UninstallCommand: String;
  QuietUninstallCommand: String;
  LegacyDir: String;
begin
  if CurStep = ssInstall then
  begin
    // Clean up legacy shortcuts if upgrading from PrivLock
    DeleteFile(ExpandConstant('{autoprograms}\PrivLock.lnk'));
    DeleteFile(ExpandConstant('{autodesktop}\PrivLock.lnk'));
    DeleteFile(ExpandConstant('{commonprograms}\PrivLock.lnk'));
    DeleteFile(ExpandConstant('{commondesktop}\PrivLock.lnk'));
  end;

  if CurStep = ssPostInstall then
  begin
    WrapperPath := ExpandConstant('{app}\{#MyAppExeName}');
    UninstallerPath := ExpandConstant('{uninstallexe}');
    UninstallCommand := Quoted(WrapperPath) + ' --safe-uninstall ' + Quoted(UninstallerPath);
    QuietUninstallCommand := UninstallCommand + ' --quiet';

    if not RegWriteStringValue(HKLM64, '{#MyAppUninstallKey}',
      'UninstallString', UninstallCommand) then
      RaiseException('Could not register the PrivGvard safe uninstall wrapper.');

    if not RegWriteStringValue(HKLM64, '{#MyAppUninstallKey}',
      'QuietUninstallString', QuietUninstallCommand) then
      RaiseException('Could not register the PrivGvard quiet safe uninstall wrapper.');

    // If upgrading from legacy directory C:\Program Files\PrivLock, clean up old binaries if different from new {app}
    LegacyDir := ExpandConstant('{autopf}\PrivLock');
    if DirExists(LegacyDir) and (CompareText(LegacyDir, ExpandConstant('{app}')) <> 0) then
    begin
      DeleteFile(AddBackslash(LegacyDir) + 'PrivLock.exe');
      DeleteFile(AddBackslash(LegacyDir) + 'unins000.exe');
      DeleteFile(AddBackslash(LegacyDir) + 'unins000.dat');
      RemoveDir(LegacyDir); // Only succeeds if folder is now empty
    end;
  end;
end;

// Every invocation, including a direct unins000.exe launch, independently proves that no process,
// user-profile session, or machine ownership claim still needs the installed recovery binary.
function InitializeUninstall: Boolean;
begin
  Result := False;
  if not TryAcquireUninstallGate then
  begin
    MsgBox('PrivGvard is running or another uninstall is in progress. Exit PrivGvard normally and try again.',
      mbCriticalError, MB_OK);
    exit;
  end;

  if AnyProfileMayHaveActiveSession or AnyPrivilegedOwnershipClaim then
  begin
    MsgBox('PrivGvard found privacy state that still requires recovery. Sign in to each affected account and start PrivGvard before uninstalling.',
      mbCriticalError, MB_OK);
    ReleaseUninstallGate;
    exit;
  end;

  if not SignalWrapperAdmission then
  begin
    MsgBox('PrivGvard could not complete the secure uninstall handoff.', mbCriticalError, MB_OK);
    ReleaseUninstallGate;
    exit;
  end;

  Result := True;
end;

procedure DeinitializeUninstall;
begin
  ReleaseUninstallGate;
end;
