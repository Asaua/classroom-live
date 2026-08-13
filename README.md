# Classroom Live

교수님이 Visual Studio에서 보고 있는 코드를 같은 교실의 학생 기기로 바로 띄워주는
도구입니다. 학생은 받아적는 대신 자기 화면에서 코드를 읽고, 원하는 파일을 직접 골라
볼 수 있습니다.

인터넷을 거치지 않습니다. 같은 와이파이 안에서만 동작합니다.

## 구성

| 위치 | 역할 |
| --- | --- |
| `host/ClassroomLive.Host` | 교수 PC에서 실행하는 ASP.NET Core 서버. 상태 보관 + 학생 화면 제공 |
| `host/ClassroomLive.Host/wwwroot` | 학생·교수 화면 (의존성 없는 정적 HTML/CSS/JS) |
| `extension/ClassroomLive.Extension` | Visual Studio 확장(VSIX). 현재 편집 중인 파일을 호스트로 보냄 |

동작 흐름:

```
Visual Studio ──(확장)──▶ 127.0.0.1:5050 ──▶ ClassroomLive.exe ──▶ 같은 와이파이의 학생 브라우저
                          X-Extension-Token          메모리에만 보관              PIN 필요
```

확장과 호스트는 `%LOCALAPPDATA%\ClassroomLive\host.json`으로 포트와 토큰을 주고받습니다.
호스트가 실행 중이어야 이 파일이 생기며, 종료하면 지워집니다.

## 빌드

호스트와 확장은 빌드 방법이 다릅니다.

**호스트** — .NET 10 SDK만 있으면 됩니다.

```bash
dotnet publish host/ClassroomLive.Host -c Release -o dist
```

`dotnet build`로도 실행 가능한 산출물이 나오지만, 배포용으로는 `publish`를 쓰세요.
`wwwroot`가 빠지면 화면이 뜨지 않으며, 이 경우 실행 시 명확한 오류 메시지가 나옵니다.

**확장(VSIX)** — Visual Studio와 "Visual Studio 확장 개발" 워크로드가 필요합니다.
`dotnet build`로는 빌드되지 않습니다.

```powershell
msbuild extension\ClassroomLive.Extension\ClassroomLive.Extension.csproj /p:Configuration=Release
```

산출물: `extension\ClassroomLive.Extension\bin\Release\ClassroomLive.Extension.vsix`

`ClassroomLive.slnx`에는 호스트만 들어 있습니다. 확장을 넣으면 솔루션 단위
`dotnet build`가 실패하기 때문입니다.

## 실행

교수용 사용법은 [`host/ClassroomLive.Host/README.txt`](host/ClassroomLive.Host/README.txt)에
정리돼 있습니다. 이 파일은 배포 폴더에 함께 복사됩니다.

환경 변수:

| 이름 | 기본값 | 설명 |
| --- | --- | --- |
| `CLASSROOM_LIVE_PORT` | `5050` | 대기 포트. 확장은 핸드셰이크 파일에서 자동으로 읽습니다 |
| `CLASSROOM_LIVE_NO_BROWSER` | – | `1`이면 시작할 때 브라우저를 열지 않습니다 |

## 학생 화면에서 할 수 있는 것

- 원하는 파일을 직접 골라 봅니다. 교수님이 다른 파일로 옮겨도 내 화면은 그대로입니다.
- **교수님 N줄** 버튼으로 교수님이 보고 있는 줄로 이동하고, 켜두면 계속 따라갑니다.
- **복사**로 현재 파일 전체를 클립보드에 담습니다.
- **줄바꿈**으로 긴 줄을 접습니다. 폰에서 좌우 스크롤 없이 읽을 때 씁니다.
- **A− / A+**로 글자 크기를 조절합니다. 설정은 다음 접속에도 유지됩니다.

## 테스트

```bash
dotnet run --project host/ClassroomLive.Host -- --self-test   # 호스트
node --test                                                    # 학생 화면 구문 강조
```

호스트 검사는 공유 보안 규칙, 화면 고정 시 갱신 정지, PIN 시도 제한, 숨긴 파일의 409
응답을 확인합니다. `node --test`는 구문 강조가 코드를 한 글자도 잃지 않는지 확인합니다.
별도 설치 없이 Node.js만 있으면 됩니다.

## 안전장치

- 현재 솔루션 폴더 안의 텍스트 코드 파일만 공유합니다. 경로 탈출은 차단됩니다.
- `.env`, `appsettings*`, `secrets.json`, `.git`, `.vs`, `bin`, `obj`, `node_modules`는 자동 차단.
- 파일 1개당 100만 자, 목록 40개까지.
- 학생은 읽기만 가능합니다. 수정·업로드 경로가 없습니다.
- **화면 고정**은 갱신만 멈춥니다. 학생에게는 마지막 화면이 그대로 보입니다.
  학생 화면에서 완전히 내리려면 교수 화면의 × 또는 Ctrl+Alt+L을 쓰세요.
- 학생 접속에는 6자리 PIN이 필요하며, 실패가 반복되면 해당 주소를 1분간 차단합니다.
- 확장 → 호스트 요청은 루프백 + 토큰을 모두 만족해야 받습니다.

한계도 알아두세요. 통신은 평문 HTTP입니다. 같은 와이파이에서 트래픽을 들여다볼 수 있는
사람은 공유 중인 코드를 볼 수 있습니다. 민감한 코드는 공유하지 마세요.
