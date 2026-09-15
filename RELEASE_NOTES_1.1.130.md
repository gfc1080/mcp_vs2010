# MCP VS2010 1.1.130 릴리즈 노트

## 주요 변경 사항

- C/C++ 새 프로젝트 생성 방식을 VS2010 Wizard 호출 중심에서 수동 `.vcxproj` 생성 방식으로 변경했습니다.
- 프로젝트 파일 생성 시 다음 구성을 기본으로 포함합니다.
  - `Debug|Win32`
  - `Release|Win32`
  - `Debug|x64`
  - `Release|x64`
- 수동 생성한 `.vcxproj`를 `_dte.Solution.AddFromFile()`로 솔루션에 추가하도록 변경했습니다.
- 빈 솔루션에 프로젝트를 추가하는 경우에도 솔루션의 x64 구성 생성을 다시 수행하도록 보완했습니다.
- `ConfigurationType`, 서브시스템, Precompiled Header, MFC/ATL, Export Symbols 설정은 프로젝트 추가 후 DTE/VCProjectEngine으로 적용합니다.
- `Dynamic Library`, `Static Library`, `Console Application`, `Windows Application` 유형을 프로젝트 설정에 반영합니다.
- 프로젝트 폴더를 생략하면 `Working folder\프로젝트 이름` 폴더를 자동으로 만들고, 명시된 폴더는 그대로 사용합니다.
- 새 솔루션 생성 후 저장 경로가 비어 있던 경우에도 요청된 경로에 `.sln` 파일을 명시적으로 저장하도록 수정했습니다.
- 저장 완료 후 브리지 discovery 정보를 갱신하여 MCP 서버에서 새 솔루션 경로를 즉시 확인할 수 있도록 했습니다.

## 설치 및 검증

- 배포 파일: `McpVs2010-Deployment-1.1.130.zip`
- VSIX: `McpVs2010.Bridge-1.1.130.vsix`
- VS2010을 모두 종료한 후 배포 폴더의 `Install-McpVs2010-Bridge.cmd`를 실행하십시오.
- 설치 후 새 VS2010 인스턴스에서 빈 솔루션을 만들고 C/C++ 프로젝트를 추가한 뒤 솔루션 구성에 `Debug|x64`와 `Release|x64`가 표시되는지 확인하십시오.

## 알려진 제한

- 실제 Visual C++ 컴파일 결과는 설치된 VS2010 도구 집합과 프로젝트별 외부 플러그인 환경에 따라 달라질 수 있습니다.
- Qt 등 외부 플러그인의 설치와 관리는 MCP VS2010이 수행하지 않습니다.
