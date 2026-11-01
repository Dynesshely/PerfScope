; PerfScope 安装包脚本（NSIS 3）
;
; 构建：makensis /DAPP_VERSION=1.0.0 /DSOURCE_DIR=<publish 目录> /DOUTPUT_FILE=<输出 exe> installer\PerfScope.nsi

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma
RequestExecutionLevel user

!ifndef APP_VERSION
  !define APP_VERSION "0.0.0"
!endif
!ifndef APP_VERSION_NUMERIC
  !define APP_VERSION_NUMERIC "0.0.0"
!endif
!ifndef SOURCE_DIR
  !error "必须指定 /DSOURCE_DIR=<publish 输出目录>"
!endif
!ifndef OUTPUT_FILE
  !define OUTPUT_FILE "PerfScope-Setup-${APP_VERSION}-win-x64.exe"
!endif
!ifndef ICON_FILE
  !define ICON_FILE "..\src\PerfScope.App\Assets\app.ico"
!endif

!define APP_NAME "PerfScope"
!define APP_PUBLISHER "Dynesshely"
!define APP_URL "https://github.com/Dynesshely/PerfScope"
!define APP_EXE "PerfScope.exe"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\PerfScope"

Name "${APP_NAME} ${APP_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\PerfScope"
InstallDirRegKey HKCU "Software\PerfScope" "InstallDir"

Icon "${ICON_FILE}"
UninstallIcon "${ICON_FILE}"

VIProductVersion "${APP_VERSION_NUMERIC}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=2052 "FileDescription" "PerfScope 安装程序"
VIAddVersionKey /LANG=2052 "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "MIT License"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"

!include "MUI2.nsh"
!include "FileFunc.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON "${ICON_FILE}"
!define MUI_UNICON "${ICON_FILE}"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即启动 PerfScope"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

Section "PerfScope" SEC_MAIN
  SetOutPath "$INSTDIR"

  ; 先关掉正在运行的实例，避免覆盖文件失败
  nsExec::Exec 'taskkill /IM ${APP_EXE} /F'
  Pop $0

  File /r "${SOURCE_DIR}\*.*"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\PerfScope" "InstallDir" "$INSTDIR"

  ; 卸载信息（Windows「应用和功能」里可见）
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "URLInfoAbout" "${APP_URL}"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"

  CreateShortCut "$SMPROGRAMS\PerfScope.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /IM ${APP_EXE} /F'
  Pop $0

  Delete "$SMPROGRAMS\PerfScope.lnk"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\PerfScope"

  MessageBox MB_YESNO|MB_ICONQUESTION "是否同时删除 PerfScope 的历史数据（$LOCALAPPDATA\PerfScope）？$\r$\n选择「否」将保留数据，便于重新安装后继续使用。" IDNO KeepData
  RMDir /r "$LOCALAPPDATA\PerfScope"
  KeepData:
SectionEnd