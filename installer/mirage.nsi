; Mirage per-user installer. No admin rights: installs to %LOCALAPPDATA%\Programs\Mirage.
Unicode true
!include "MUI2.nsh"

!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef SRCDIR
  !define SRCDIR "..\out\publish"
!endif
!ifndef OUTFILE
  !define OUTFILE "..\dist\Mirage-Setup-${VERSION}.exe"
!endif

Name "Mirage"
OutFile "${OUTFILE}"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\Mirage"
InstallDirRegKey HKCU "Software\Mirage" "InstallDir"
SetCompressor /SOLID lzma
BrandingText "Mirage ${VERSION}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "Mirage"
VIAddVersionKey "FileDescription" "Mirage launcher setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "MIT License"

!define MUI_ICON "..\assets\mirage.ico"
!define MUI_UNICON "..\assets\mirage.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Install Mirage"
!define MUI_WELCOMEPAGE_TEXT "Mirage is a fast, keyboard-first launcher for Windows.$\r$\n$\r$\nIt installs just for you, so no administrator rights are needed.$\r$\n$\r$\nAfter installing, press Alt+Space to open it."
!define MUI_FINISHPAGE_RUN "$INSTDIR\Mirage.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Start Mirage now"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Mirage"

Section "Mirage" SecMain
  SetShellVarContext current
  ; Close a running copy so the exe can be replaced (only your own processes; no admin needed)
  nsExec::Exec 'taskkill /IM Mirage.exe /F'
  Sleep 400

  SetOutPath "$INSTDIR"
  File "${SRCDIR}\Mirage.exe"
  File "..\LICENSE"
  WriteUninstaller "$INSTDIR\Uninstall Mirage.exe"

  CreateShortCut "$SMPROGRAMS\Mirage.lnk" "$INSTDIR\Mirage.exe" "" "$INSTDIR\Mirage.exe" 0

  WriteRegStr HKCU "Software\Mirage" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "Mirage"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "Mirage"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\Mirage.exe"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall Mirage.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall Mirage.exe" /S'
  WriteRegStr HKCU "${UNINST_KEY}" "URLInfoAbout" "https://github.com/hanialnaber/mirage-launcher"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" 160000
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  nsExec::Exec 'taskkill /IM Mirage.exe /F'
  Sleep 400
  Delete "$INSTDIR\Mirage.exe"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\Uninstall Mirage.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\Mirage.lnk"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Mirage"
  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\Mirage"
  ; Your settings, notes and snippets stay in %APPDATA%\Mirage in case you reinstall.
SectionEnd
