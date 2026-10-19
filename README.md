# SunChat

Windows LAN 메신저 (WPF, .NET 10). 같은 네트워크의 사용자와 메시지·파일을 주고받습니다.

## 기능
- **자동 사용자 탐색** — UDP 브로드캐스트로 같은 서브넷 사용자 자동 표시.
- **IP로 연결** — 다른 대역/원격 상대를 IP:포트로 수동 연결.
- **메시지·파일 전송** — TCP, 상대별 대화.
- **그룹** — 그룹 지정 + "같은 그룹만 보기" 필터.
- **상대별 대화 기록** — `%AppData%\SunChat\chat`에 저장, 보관일(1~180일) 자동 정리.
- **온라인/오프라인 표시** — TCP 연결 확인으로 실제 구동 여부 판별(검정/회색). 새로고침 버튼 + 창 복원 시 자동 갱신.
- **트레이 상주** — 창 닫아도 백그라운드 유지, 수신 시 우하단 토스트 알림.
- **중복 실행 방지**, 윈도우 시작 시 자동 실행(선택), 환경설정 영구 저장.

## 빌드
```bash
dotnet build SunChat.csproj -c Release
```

## 배포본 (단일 exe, .NET 설치 불필요)
```bash
dotnet publish SunChat.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o dist
```

## 포트
- UDP 51000 — 사용자 탐색(브로드캐스트)
- TCP 51001 — 메시지·파일 (설정에서 변경 가능)

다른 대역/방화벽 너머에서 쓰려면 해당 PC의 TCP 포트를 방화벽에서 허용하세요.

## 요구사항
Windows 10 이상 (x64).
