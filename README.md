# MCP VS2010

### * 이 프로젝트는 Codex를 사용하여 만들었습니다. *

Visual Studio 2010 내부의 실제 DTE 빌드를 Codex에서 호출하기 위한 Windows 전용 MCP 서버입니다.

구성은 두 프로세스로 분리됩니다.

- `McpVs2010.Server`: .NET 10 기반 Streamable HTTP MCP 서버
- `McpVs2010.Bridge-<버전>.vsix`: VS2010 `devenv.exe` 내부에서 실행되는 .NET Framework 4.0 VSPackage

두 구성 요소는 사용자 세션의 Named Pipe로 통신합니다. VSIX는 Qt를 포함한 외부 플러그인을 탐지하거나 설치하거나 버전을 검사하지 않습니다. 빌드는 현재 VS2010 환경에서 실행되며, 플러그인 관련 실패도 VS2010이 출력한 메시지를 그대로 수집합니다.

## 빌드

PowerShell에서 다음을 실행합니다.

```powershell
.\build.ps1
```

빌드를 시작하면 `artifacts` 폴더 내부가 먼저 모두 정리된 후 새 산출물이 생성됩니다.

빌드 산출물만 정리하려면 다음을 실행합니다.

```powershell
.\clean.ps1
```

소스 프로젝트의 `bin`/`obj`까지 정리하려면 `-AllBuildOutputs`를 사용합니다. 실제 삭제 없이 대상만 확인하려면 `-WhatIf`를 추가합니다.

현재 게시된 MCP 서버가 실행 중이라 서버 파일을 유지한 채 VSIX만 다시 만들려면 다음을 사용합니다.

```powershell
.\build.ps1 -SkipServer -SkipRestore
```

설치된 VSIX와 호환되는 버전에서 Streamable HTTP 서버만 다시 게시하려면 다음을 사용합니다.

```powershell
.\build.ps1 -SkipBridge -SkipRestore
```

산출물:

- `VERSION.DEF`의 `VERSION` 값을 읽은 후 마지막 숫자를 빌드마다 1씩 증가시켜 서버, VSIX 및 배포 파일명에 사용합니다.
- 버전은 `major.minor.patch` 3자리 형식을 사용합니다. 예를 들어 `1.1.1`에서 시작하면 첫 빌드는 `1.1.2`입니다.

서버와 VSIX는 솔루션 열기 및 Project Only 명령을 지원합니다. 설치된 서버 파일은 `%LOCALAPPDATA%\McpVs2010`에 저장됩니다. VS2010이 실행되면 VSIX가 이 위치의 MCP 서버를 자동으로 시작하며, 서버는 트레이의 `Exit`를 선택할 때까지 계속 실행됩니다. HTTP Stream 포트는 사용자 레지스트리 `HKCU\Software\McpVs2010\HttpStreamPort`(REG_DWORD)에 저장됩니다.

빌드 스크립트는 `VS100COMNTOOLS`와 32비트 Visual Studio 레지스트리를 순서대로 확인합니다. `vswhere`만으로 VS2010을 판단하지 않습니다.

생성된 VSIX 구조와 MCP 핸드셰이크 및 도구 노출을 다시 검사하려면 다음을 실행합니다.

```powershell
.\scripts\Test-Artifacts.ps1
```

## VSIX 설치

### 다른 사용자에게 배포

소스 저장소가 없는 PC에는 `artifacts\McpVs2010-Deployment-1.1.30.zip` 또는 `McpVs2010-Deployment-Latest.zip`을 전달합니다. ZIP을 압축 해제한 뒤 `Install-McpVs2010-Bridge.cmd`를 실행하면 VSIX와 사용자용 MCP 서버 파일이 설치됩니다. 배포 폴더의 `README-Deployment.txt`에 사전 조건, 자동 실행 동작과 포트 변경 방법이 정리되어 있습니다.

배포 설치에는 Visual Studio 2010의 VSIX 지원과 .NET 10 런타임이 필요합니다. Qt 등 프로젝트별 외부 플러그인은 배포 패키지가 설치하거나 검사하지 않으며, 사용자가 해당 PC에서 별도로 관리합니다. 자체 소스 코드는 `LICENSE`, 외부 의존성 라이선스는 `THIRD-PARTY-NOTICES.txt`를 참고하십시오.

VS2010을 모두 종료한 후 다음 스크립트를 직접 실행합니다.

```powershell
.\scripts\Install-Vsix.ps1
```

탐색기에서 실행하려면 프로젝트 루트의 `Install-McpVs2010-Bridge.cmd`를 더블클릭합니다.

`.vsix` 파일을 더블클릭하면 Visual Studio 2022/2026 설치기가 연결되어 VS2010의 기존 활성 등록을 남길 수 있습니다. VSIX를 직접 열지 말고 반드시 위 스크립트를 사용하십시오. 스크립트는 VS2010 설치 폴더의 `VSIXInstaller.exe`를 명시적으로 실행합니다.

VS2010 설치기를 직접 지정해 일반 설치 창을 열 수도 있습니다. 다만 이전 업데이트의 중복 폴더나 잘못된 활성 등록이 이미 있으면 정리 기능이 포함된 `Install-Vsix.ps1`을 사용해야 합니다.

```powershell
& 'C:\Program Files (x86)\Microsoft Visual Studio 10.0\Common7\IDE\VSIXInstaller.exe' `
  'D:\WORK\ai_work\codex\mcp_vs2010\artifacts\McpVs2010.Bridge-1.1.30.vsix'
```

설치 후 VS2010을 다시 시작합니다. 브리지가 로드되면 다음 위치에 인스턴스 검색 파일이 생성됩니다.

```text
%LOCALAPPDATA%\McpVs2010\instances\<devenv-pid>.json
```

설치 스크립트는 서버 실행 파일과 종속 파일을 다음 고정 경로에 복사합니다.

```text
%LOCALAPPDATA%\McpVs2010\McpVs2010.Server.exe
```

VS2010의 `Tools > MCP server` 메뉴를 선택하면 대화창이 표시됩니다. 대화창에서 현재 접속 URL과 서버 상태를 확인하고 `STOP`, `START`, 포트 입력 및 `Apply`를 사용할 수 있습니다. 포트를 변경하면 레지스트리 저장 후 실행 중인 서버가 자동으로 재시작됩니다.

설치 스크립트는 확장 ID `McpVs2010.Bridge`의 기존 사용자 설치 폴더, 활성 레지스트리 등록과 VS2010 확장 캐시를 정리한 뒤 지정한 VSIX를 자동 설치합니다. 마지막에는 새 버전의 매니페스트뿐 아니라 `EnabledExtensions` 활성 등록 경로와 버전도 다시 확인합니다. 따라서 같은 버전 재설치, 상위 버전 업데이트와 실패한 이전 업데이트의 잔여 폴더 정리를 모두 지원합니다. 관리자 범위 설치본을 발견하면 임의로 제거하지 않고 정확한 경로와 함께 중단합니다. 실제 변경 없이 교체 계획만 확인하려면 다음을 실행합니다.

```powershell
.\scripts\Install-Vsix.ps1 -WhatIf
```

## 서버 자동 실행 및 접속

기본적으로 loopback 주소에만 바인딩하며 MCP 엔드포인트는 다음과 같습니다.

```text
http://127.0.0.1:3010/stream
```

배포 설치 후 서버 실행 파일은 다음 위치에 있습니다. 이 파일은 VS2010 실행 시 VSIX가 자동으로 시작하므로 사용자가 별도로 실행할 필요가 없습니다.

```text
%LOCALAPPDATA%\McpVs2010\McpVs2010.Server.exe
```

기본 HTTP Stream 포트는 3010이며, VS2010의 `Tools > MCP server` 대화상자에서 변경합니다.

```text
HKCU\Software\McpVs2010\HttpStreamPort = 3010 (REG_DWORD)
```

허용 범위는 `1`~`65535`입니다. 레지스트리 값이 없으면 기본값 `3010`을 사용하며, 값이 잘못되면 서버가 원인을 표시하고 종료합니다. 바인딩 주소와 MCP 경로는 각각 `127.0.0.1`, `/stream`입니다.

진단 목적으로 직접 실행하거나 일시적으로 다른 URL을 적용하려면 ASP.NET Core의 `--urls` 옵션을 사용할 수 있습니다. Host와 Origin은 loopback 주소만 허용합니다.

```powershell
%LOCALAPPDATA%\McpVs2010\McpVs2010.Server.exe --urls http://127.0.0.1:3020
```

## Codex 연결

Codex의 프로젝트 또는 사용자 `config.toml`에 Streamable HTTP URL을 지정합니다.

```toml
[mcp_servers.vs2010]
url = "http://127.0.0.1:3010/stream"
startup_timeout_sec = 20
tool_timeout_sec = 3600
```

Codex는 VSIX가 자동으로 실행한 서버의 URL에 연결합니다. 먼저 VS2010과 MCP VS2010 Bridge가 실행 중이어야 합니다.
레지스트리의 포트를 바꾸면 위 URL의 포트도 같은 값으로 변경해야 합니다. 메뉴에서 포트를 변경하면 서버가 자동으로 재시작됩니다.

## MCP 도구

- `list_vs2010_instances`: 브리지가 로드된 VS2010 인스턴스 목록
- `list_vs2010_recent_projects`: VS2010의 최근 프로젝트 및 솔루션 MRU 목록
- `open_vs2010_recent_solution`: 최근 목록의 지정 순번 솔루션 열기. 다른 솔루션이 열려 있으면 저장 후 닫음
- `get_vs2010_state`: 열린 솔루션, 구성, 중첩 프로젝트, 빌드 상태
- `build_vs2010_solution`: 솔루션 전체 `clean`, `build`, `rebuild`
- `build_vs2010_project`: 선택한 Visual C++ 프로젝트만 `clean`, `build`, `rebuild`
- `cancel_vs2010_build`: 진행 중인 DTE 빌드 취소 요청

VS2010 인스턴스가 하나이면 `processId`를 생략할 수 있습니다. 여러 개라면 목록에서 PID를 선택해야 합니다.

`build_vs2010_project`의 `project`에는 `get_vs2010_state`가 반환한 프로젝트 `name`, `uniqueName` 또는 프로젝트 파일 전체 경로를 지정합니다. 같은 이름이 둘 이상이면 `uniqueName`이나 전체 경로가 필요합니다. 이 도구는 VS2010의 **Build > Project Only** 명령을 실행하므로 Visual C++ 프로젝트만 지원하고 프로젝트 의존성이나 솔루션 파일을 함께 처리하지 않습니다.

## 오류 및 출력 보존

빌드 결과에는 다음이 포함됩니다.

- `failedProjects`: `SolutionBuild.LastBuildInfo` 원본 값
- `scope`, `operation`, `command`: 실제 실행 범위, 작업과 VS2010 명령 이름
- `projectName`, `projectUniqueName`, `projectFullName`: Project Only 대상 식별 정보
- `errors`: VS2010 Error List의 수준, 설명, 프로젝트, 파일, 줄, 열
- `outputPanes`: 빌드 전후에 변경된 Build Output pane의 원문
- `captureErrors`: Output 또는 Error List를 읽지 못한 경우의 별도 진단

MCP 서버는 오류 내용을 일반화하거나 플러그인 누락으로 임의 분류하지 않습니다.

## 현재 검증 경계

`build.ps1`로 서버와 VSIX의 컴파일 및 패키징을 검증할 수 있습니다. 실제 DTE 빌드 검증은 VSIX를 설치하고 VS2010에서 대상 솔루션을 연 뒤 수행해야 합니다.
