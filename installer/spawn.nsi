; SPAWN per-user installer (NSIS 3.13), compiled by scripts/package.py with
;   makensis -INPUTCHARSET UTF8 -WX -DSPAWN_IDENTITY=... installer/spawn.nsi
; Invariants:
;   1. Per-user, no UAC prompt: %LOCALAPPDATA%\Programs\<identity> (RequestExecutionLevel user).
;   2. Each identity (spawn, spawn-dev, spawn-test, spawn-dev-test) has its own uninstall key
;      HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\io.github.chialecode.<identity>,
;      mutexes, shortcuts and folder.
;   3. Never deletes user data (%LOCALAPPDATA%\spawn*): the uninstaller removes exactly the files
;      this version installed (generated list), then the folder only when it is empty.
;   4. Never force-kills: asks a running copy to --quit-for-update, waits 30 s, otherwise stops.
;   5. Spawn.TrainerWorker stays under Workers\Trainer\ (never flattened); THIRD-PARTY-NOTICES.txt ships.
;   6. /RELAUNCH (silent installs only) starts <identity>.exe --after-update once the update is done.
;   7. One installation per identity: an update without /D= goes into the installed folder
;      (InstallDirRegKey); a /D= naming another folder uninstalls the old copy first; a folder
;      holding another program's Uninstall.exe is refused (exit code 7) and left untouched.
;   8. A file that cannot be written (held open by another program) stops the installer with
;      exit code 8 before the uninstaller and the uninstall entry are written.
; Command line: /S silent; /D=<folder> (last, unquoted) picks the folder; /RELAUNCH as above.

!if "${NSIS_VERSION}" != "v3.13"
  !error "SPAWN installer requires NSIS 3.13, not ${NSIS_VERSION}; scripts/package.py fetches the pinned build"
!endif

!macro SPAWN_REQUIRE NAME
  !ifndef ${NAME}
    !error "${NAME} must be defined (scripts/package.py passes it)"
  !endif
!macroend
!insertmacro SPAWN_REQUIRE SPAWN_IDENTITY       ; spawn, spawn-dev, spawn-test, spawn-dev-test
!insertmacro SPAWN_REQUIRE SPAWN_APP_ID         ; io.github.chialecode.<identity>
!insertmacro SPAWN_REQUIRE SPAWN_DISPLAY_NAME   ; SPAWN, SPAWN Dev, ...
!insertmacro SPAWN_REQUIRE SPAWN_VERSION        ; DisplayVersion, e.g. 0.1.1-dev.202611240000
!insertmacro SPAWN_REQUIRE SPAWN_FILE_VERSION   ; four numbers, e.g. 0.1.1.0
!insertmacro SPAWN_REQUIRE SPAWN_ICON           ; channel icon
!insertmacro SPAWN_REQUIRE SPAWN_SOURCE_DIR     ; staged app folder
!insertmacro SPAWN_REQUIRE SPAWN_OUT_FILE       ; installer path
!insertmacro SPAWN_REQUIRE SPAWN_FILES_NSH      ; generated: SPAWN_REMOVE_INSTALLED_FILES
!insertmacro SPAWN_REQUIRE SPAWN_CREATE_SHORTCUTS   ; 1 or 0 (test identities: 0)
!insertmacro SPAWN_REQUIRE SPAWN_DESKTOP_ON_UPDATE  ; 1: silent updates make both shortcuts again (dev)

!define SPAWN_EXE "${SPAWN_IDENTITY}.exe"
!define SPAWN_UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${SPAWN_APP_ID}"
; 150 x 200 ms = 30 s, the wait scripts/package.py and the in-app updater allow.
!define SPAWN_QUIT_WAIT_TICKS 150

; Exit codes besides NSIS's own (0 done, 1 cancelled, 2 aborted).
!define SPAWN_EXIT_STILL_RUNNING 4  ; SPAWN did not exit within 30 s (or refused)
!define SPAWN_EXIT_QUIT_FAILED 5    ; SPAWN runs, but its executable to ask was not found
!define SPAWN_EXIT_SETUP_RUNNING 6  ; another installer of this identity is running
!define SPAWN_EXIT_FOLDER_TAKEN 7   ; the folder holds another program's Uninstall.exe
!define SPAWN_EXIT_FILES_NOT_WRITTEN 8 ; a file could not be written (held open elsewhere)

Unicode true
ManifestDPIAware true
; zlib for every channel: its module is under the zlib licence like the rest of the stub, while
; NSIS's LZMA module is CPL-1.0 (weak copyleft, statically inside every installer), outside the
; licence rules (docs/dev-rules/architecture-and-open-source.md section 2).
!ifdef SPAWN_FAST_COMPRESSION
  SetCompressor zlib
!else
  SetCompressor /SOLID zlib
!endif
RequestExecutionLevel user
Name "${SPAWN_DISPLAY_NAME}"
OutFile "${SPAWN_OUT_FILE}"
; The identity's own folder on a first install, the installed folder on an update; /D= overrides
; both (no directory page).
InstallDir "$LOCALAPPDATA\Programs\${SPAWN_IDENTITY}"
InstallDirRegKey HKCU "${SPAWN_UNINSTALL_KEY}" "InstallLocation"
BrandingText " "
ShowInstDetails nevershow
ShowUninstDetails nevershow

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "Sections.nsh"
!include "x64.nsh"

!define MUI_ICON "${SPAWN_ICON}"
!define MUI_UNICON "${SPAWN_ICON}"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${SPAWN_EXE}"
!if ${SPAWN_CREATE_SHORTCUTS} == 1
  !define MUI_COMPONENTSPAGE_NODESC
  !insertmacro MUI_PAGE_COMPONENTS
!endif
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

VIProductVersion "${SPAWN_FILE_VERSION}"
VIFileVersion "${SPAWN_FILE_VERSION}"
VIAddVersionKey /LANG=${LANG_SIMPCHINESE} "ProductName" "${SPAWN_DISPLAY_NAME}"
VIAddVersionKey /LANG=${LANG_SIMPCHINESE} "ProductVersion" "${SPAWN_VERSION}"
VIAddVersionKey /LANG=${LANG_SIMPCHINESE} "FileVersion" "${SPAWN_FILE_VERSION}"
VIAddVersionKey /LANG=${LANG_SIMPCHINESE} "FileDescription" "${SPAWN_DISPLAY_NAME} Per-User Installer"
VIAddVersionKey /LANG=${LANG_SIMPCHINESE} "CompanyName" "SPAWN Contributors"
VIAddVersionKey /LANG=${LANG_SIMPCHINESE} "LegalCopyright" "Copyright (C) SPAWN Contributors. Licensed under Apache-2.0."

; Deletes one installed file. A file the kernel still maps after SPAWN exited cannot be deleted
; but can be renamed: it moves to .retired in the same folder (same volume), which the next
; install or the uninstall removes.
!macro SPAWN_REMOVE_FILE REL FLAT
  ClearErrors
  Delete "$INSTDIR\${REL}"
  ${If} ${Errors}
  ${AndIf} ${FileExists} "$INSTDIR\${REL}"
    CreateDirectory "$INSTDIR\.retired"
    System::Call 'kernel32::GetTickCount() i .r9'
    Rename "$INSTDIR\${REL}" "$INSTDIR\.retired\${FLAT}.$9"
  ${EndIf}
!macroend

; SPAWN_REMOVE_INSTALLED_FILES: every file this version installs, then its folders deepest first.
!include "${SPAWN_FILES_NSH}"

Var FirstInstall

; Leaves "1" on the stack when $INSTDIR holds an Uninstall.exe that is not this identity's installed
; copy (another program, or another SPAWN identity), else "0". Such an uninstaller is never run or
; overwritten.
Function SpawnFolderTaken
  Push $0
  Push $1
  StrCpy $1 "0"
  ${If} ${FileExists} "$INSTDIR\Uninstall.exe"
    ReadRegStr $0 HKCU "${SPAWN_UNINSTALL_KEY}" "InstallLocation"
    ${If} $0 != $INSTDIR
      StrCpy $1 "1"
      ; Our own copy whose uninstall entry is gone (an interrupted install, a
      ; registry cleaner) is updated in place instead.
      ${If} $0 == ""
      ${AndIf} ${FileExists} "$INSTDIR\${SPAWN_EXE}"
        StrCpy $1 "0"
      ${EndIf}
    ${EndIf}
  ${EndIf}
  Exch $1
  Exch
  Pop $0
FunctionEnd

; Leaves "1" on the stack when SPAWN of this identity runs: any of its mutexes exists (or
; exists and cannot be opened), else "0".
!macro SPAWN_IS_RUNNING_FUNCTION UN
Function ${UN}SpawnIsRunning
  Push $0
  Push $1
  Push $2
  StrCpy $2 "0"
  !insertmacro SPAWN_PROBE_MUTEX "${SPAWN_IDENTITY}-instance"
  !insertmacro SPAWN_PROBE_MUTEX "Local\${SPAWN_IDENTITY}-instance"
  !insertmacro SPAWN_PROBE_MUTEX "Global\${SPAWN_IDENTITY}-instance"
  StrCpy $0 $2
  Pop $2
  Pop $1
  Exch $0
FunctionEnd
!macroend
!macro SPAWN_PROBE_MUTEX NAME
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "${NAME}") p.r0 ?e'
  Pop $1
  ${If} $0 <> 0
    System::Call 'kernel32::CloseHandle(p r0)'
    StrCpy $2 "1"
  ${ElseIf} $1 = 5
    StrCpy $2 "1"
  ${EndIf}
!macroend
!insertmacro SPAWN_IS_RUNNING_FUNCTION ""
!insertmacro SPAWN_IS_RUNNING_FUNCTION "un."

; Asks a running SPAWN to save and quit (--quit-for-update) and waits up to 30 s for every
; mutex to go; it is never ended by force. Silent runs go ahead without asking, the others ask
; first. When SPAWN keeps running, the installer or uninstaller quits with SPAWN_EXIT_*.
!macro SPAWN_CLOSE_APP_FUNCTION UN
Function ${UN}SpawnCloseApp
  Push $0
  Push $1
  Push $2
  Call ${UN}SpawnIsRunning
  Pop $0
  ${If} $0 == "1"
    MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "${SPAWN_DISPLAY_NAME} 正在运行。点“确定”让它保存后退出并继续，点“取消”退出。" /SD IDOK IDOK spawn_quit
    SetErrorLevel 1
    Quit
    spawn_quit:
    ReadRegStr $1 HKCU "${SPAWN_UNINSTALL_KEY}" "InstallLocation"
    ${If} $1 == ""
      StrCpy $1 "$INSTDIR"
    ${EndIf}
    StrCpy $1 "$1\${SPAWN_EXE}"
    ${IfNot} ${FileExists} "$1"
      MessageBox MB_OK|MB_ICONSTOP "${SPAWN_DISPLAY_NAME} 正在运行，但找不到 $1 来请它退出。请先从托盘退出它再试。" /SD IDOK
      SetErrorLevel ${SPAWN_EXIT_QUIT_FAILED}
      Quit
    ${EndIf}
    ; The exit code does not matter: what counts is whether the mutexes go within 30 s.
    ExecWait '"$1" --quit-for-update'
    StrCpy $2 0
    ${Do}
      Call ${UN}SpawnIsRunning
      Pop $0
      ${If} $0 != "1"
        ${Break}
      ${EndIf}
      ${If} $2 >= ${SPAWN_QUIT_WAIT_TICKS}
        MessageBox MB_OK|MB_ICONSTOP "${SPAWN_DISPLAY_NAME} 仍在运行或正忙（没有强制结束它）。请关闭它后重试。" /SD IDOK
        SetErrorLevel ${SPAWN_EXIT_STILL_RUNNING}
        Quit
      ${EndIf}
      Sleep 200
      IntOp $2 $2 + 1
    ${Loop}
  ${EndIf}
  Pop $2
  Pop $1
  Pop $0
FunctionEnd
!macroend
!insertmacro SPAWN_CLOSE_APP_FUNCTION ""
!insertmacro SPAWN_CLOSE_APP_FUNCTION "un."

!if ${SPAWN_CREATE_SHORTCUTS} == 1
Section "开始菜单快捷方式" SecStartMenu
SectionEnd
Section "桌面快捷方式" SecDesktop
SectionEnd
!endif

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "${SPAWN_DISPLAY_NAME} 只支持 64 位 Windows。" /SD IDOK
    Abort
  ${EndIf}
  ; One installer per identity at a time.
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "${SPAWN_IDENTITY}-setup") p .r1 ?e'
  Pop $0
  ${If} $0 = 183
    MessageBox MB_OK|MB_ICONSTOP "另一个 ${SPAWN_DISPLAY_NAME} 安装程序正在运行。" /SD IDOK
    SetErrorLevel ${SPAWN_EXIT_SETUP_RUNNING}
    Quit
  ${EndIf}

  Call SpawnFolderTaken
  Pop $0
  ${If} $0 == "1"
    MessageBox MB_OK|MB_ICONSTOP "$INSTDIR 里已装有别的程序。请换一个文件夹再安装 ${SPAWN_DISPLAY_NAME}。" /SD IDOK
    SetErrorLevel ${SPAWN_EXIT_FOLDER_TAKEN}
    Quit
  ${EndIf}

  ReadRegStr $0 HKCU "${SPAWN_UNINSTALL_KEY}" "UninstallString"
  ${If} $0 == ""
    StrCpy $FirstInstall "1"
  ${Else}
    StrCpy $FirstInstall "0"
  ${EndIf}

!if ${SPAWN_CREATE_SHORTCUTS} == 1
  ; A first install makes both shortcuts; an update keeps the ones that are there, and the
  ; development channel's silent update makes both again (a sign that a new build arrived).
  ${If} $FirstInstall == "1"
    !insertmacro SelectSection ${SecStartMenu}
    !insertmacro SelectSection ${SecDesktop}
  ${Else}
    !insertmacro UnselectSection ${SecStartMenu}
    !insertmacro UnselectSection ${SecDesktop}
    ${If} ${FileExists} "$SMPROGRAMS\${SPAWN_DISPLAY_NAME}.lnk"
      !insertmacro SelectSection ${SecStartMenu}
    ${EndIf}
    ${If} ${FileExists} "$DESKTOP\${SPAWN_DISPLAY_NAME}.lnk"
      !insertmacro SelectSection ${SecDesktop}
    ${EndIf}
    !if ${SPAWN_DESKTOP_ON_UPDATE} == 1
      ${If} ${Silent}
        !insertmacro SelectSection ${SecStartMenu}
        !insertmacro SelectSection ${SecDesktop}
      ${EndIf}
    !endif
  ${EndIf}
!endif

  Call SpawnCloseApp
FunctionEnd

Section "-Install"
  ReadRegStr $0 HKCU "${SPAWN_UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 == ""
  ${OrIf} $0 == $INSTDIR
    ; Installed here (or our own copy here lost its entry; the folder check refused anything else).
    ${If} ${FileExists} "$INSTDIR\Uninstall.exe"
      ; Files a previous update had to move aside are free by now.
      RMDir /r "$INSTDIR\.retired"
      ; The installed version's uninstaller removes exactly its own files (/UPDATE keeps the
      ; shortcuts and the uninstall entry), so files this version no longer ships do not linger.
      ; _?= runs it in place and waits.
      ClearErrors
      ExecWait '"$INSTDIR\Uninstall.exe" /S /UPDATE _?=$INSTDIR'
      ${If} ${Errors}
        DetailPrint "旧版本的卸载程序没能运行，旧版本独有的文件可能留在 $INSTDIR。"
      ${EndIf}
    ${EndIf}
  ${ElseIf} ${FileExists} "$0\Uninstall.exe"
    ; Installed in another folder (a different /D=): uninstall that copy completely first, so one
    ; copy and one uninstall entry remain.
    ClearErrors
    ExecWait '"$0\Uninstall.exe" /S _?=$0' $1
    ${If} ${Errors}
    ${OrIf} $1 <> 0
      ; Keep its uninstaller: the old copy is still there and can be removed later.
      DetailPrint "没能卸载 $0 里的旧副本。"
    ${Else}
      Delete "$0\Uninstall.exe"
      RMDir "$0"
    ${EndIf}
  ${EndIf}

  SetOutPath "$INSTDIR"
  ; Under /S a file that cannot be written is skipped (NSIS answers its own prompt with Ignore)
  ; and only the error flag says so: stop before the entry claims the new version.
  ClearErrors
  File /r "${SPAWN_SOURCE_DIR}\*"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "有文件被别的程序占用，没能写入 $INSTDIR。请关闭占用它的程序后重新运行安装程序。" /SD IDOK
    SetErrorLevel ${SPAWN_EXIT_FILES_NOT_WRITTEN}
    Quit
  ${EndIf}
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "${SPAWN_UNINSTALL_KEY}" "DisplayName" "${SPAWN_DISPLAY_NAME}"
  WriteRegStr HKCU "${SPAWN_UNINSTALL_KEY}" "DisplayVersion" "${SPAWN_VERSION}"
  WriteRegStr HKCU "${SPAWN_UNINSTALL_KEY}" "DisplayIcon" '"$INSTDIR\${SPAWN_EXE}"'
  WriteRegStr HKCU "${SPAWN_UNINSTALL_KEY}" "Publisher" "SPAWN Contributors"
  WriteRegStr HKCU "${SPAWN_UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/chialecode/spawn"
  WriteRegStr HKCU "${SPAWN_UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${SPAWN_UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${SPAWN_UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${SPAWN_UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${SPAWN_UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${SPAWN_UNINSTALL_KEY}" "EstimatedSize" "$0"
SectionEnd

!if ${SPAWN_CREATE_SHORTCUTS} == 1
; After the files: make the chosen shortcuts and remove the ones left unchosen.
Section "-Shortcuts"
  SetOutPath "$INSTDIR"
  ${If} ${SectionIsSelected} ${SecStartMenu}
    CreateShortCut "$SMPROGRAMS\${SPAWN_DISPLAY_NAME}.lnk" "$INSTDIR\${SPAWN_EXE}" "" "$INSTDIR\${SPAWN_EXE}"
  ${Else}
    Delete "$SMPROGRAMS\${SPAWN_DISPLAY_NAME}.lnk"
  ${EndIf}
  ${If} ${SectionIsSelected} ${SecDesktop}
    CreateShortCut "$DESKTOP\${SPAWN_DISPLAY_NAME}.lnk" "$INSTDIR\${SPAWN_EXE}" "" "$INSTDIR\${SPAWN_EXE}"
  ${Else}
    Delete "$DESKTOP\${SPAWN_DISPLAY_NAME}.lnk"
  ${EndIf}
SectionEnd
!endif

Section "-Relaunch"
  ${If} ${Silent}
    ${GetParameters} $0
    ClearErrors
    ${GetOptions} $0 "/RELAUNCH" $1
    ${IfNot} ${Errors}
      SetOutPath "$INSTDIR"
      Exec '"$INSTDIR\${SPAWN_EXE}" --after-update'
    ${EndIf}
  ${EndIf}
SectionEnd

; Deletes the shortcut on the stack, trying again for 5 s while Explorer still holds a new
; shortcut open; succeeds when there is none.
Function un.SpawnDeleteShortcut
  Exch $0
  Push $1
  StrCpy $1 50
  ${Do}
    ClearErrors
    Delete "$0"
    ${IfNot} ${Errors}
      ${Break}
    ${EndIf}
    IntOp $1 $1 - 1
    ${If} $1 <= 0
      ${Break}
    ${EndIf}
    Sleep 100
  ${Loop}
  Pop $1
  Pop $0
FunctionEnd

Function un.onInit
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/UPDATE" $1
  ${If} ${Errors}
    Call un.SpawnCloseApp
  ${EndIf}
FunctionEnd

Section "Uninstall"
  !insertmacro SPAWN_REMOVE_INSTALLED_FILES

  ; An update (run by the next installer) stops here: the installer then writes the new
  ; files, the uninstaller, the shortcuts and the entry.
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/UPDATE" $1
  ${If} ${Errors}
    Push "$SMPROGRAMS\${SPAWN_DISPLAY_NAME}.lnk"
    Call un.SpawnDeleteShortcut
    Push "$DESKTOP\${SPAWN_DISPLAY_NAME}.lnk"
    Call un.SpawnDeleteShortcut
    DeleteRegKey HKCU "${SPAWN_UNINSTALL_KEY}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir /r "$INSTDIR\.retired"
    ; Only when empty: anything the user put there stays; user data lives elsewhere.
    RMDir "$INSTDIR"
  ${EndIf}
SectionEnd
