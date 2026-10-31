# 빠른 시작 (한국어)

목표: **서버 3분, 클라이언트 1분.** 둘 다 한 줄짜리 명령으로 끝나는 것이 이 툴의 존재 이유입니다.

---

## 1. 서버 (3분)

Docker가 설치되어 있어야 합니다. 리포를 clone한 뒤:

```bash
cp .env.example .env
```

`.env`를 열고 토큰을 하나 정합니다. 길고 무작위한 문자열이면 충분합니다:

```
SCENECOLLAB_TOKEN=여기에-길고-무작위한-문자열
```

```bash
docker compose up -d
curl http://localhost:8080/health
```

`{"status":"ok", ...}` 가 나오면 서버는 끝입니다.

> **토큰이 없으면 서버는 일부러 실행되지 않습니다.** 인증 없는 씬 서버가 인터넷에 열리면
> 씬 전체가 유출되는 사고가 되기 때문에, 안전하지 않은 경로는 `--insecure-dev`를 직접
> 타이핑해야만 열립니다.

### 기본 바인드 주소는 `127.0.0.1` 입니다

집 안(같은 공유기)에서만 쓸 때는 그대로 두면 됩니다. 외부에서 접속해야 한다면
TLS를 처리하는 리버스 프록시(Nginx Proxy Manager 등) 뒤에 두고, WebSocket 업그레이드를
`/ws`로 전달하세요. 서버 자체를 `0.0.0.0`에 직접 노출하는 것은 권장하지 않습니다.

Nginx Proxy Manager 예시:

| 항목 | 값 |
|---|---|
| Scheme | `http` |
| Forward Hostname | 서버가 도는 PC의 LAN IP |
| Forward Port | `8080` |
| Websockets Support | **켜기** |
| SSL | Let's Encrypt, Force SSL |

그러면 접속 주소는 `wss://collab.example.com/ws`가 됩니다.

---

## 2. 클라이언트 (1분)

`Packages/manifest.json`에 한 줄 추가:

```jsonc
"com.obsihill.scenecollab": "https://github.com/Obsihill/scene-collab.git?path=unity/com.obsihill.scenecollab#v0.2.0"
```

Unity가 패키지를 받아오면 메뉴에 **Tools > Scene Collab** 이 생깁니다.

1. **Address**: `ws://localhost:8080/ws` (또는 `wss://.../ws`)
2. **Room**: 팀이 정한 이름. 같은 이름을 쓰는 사람끼리만 만납니다.
3. **Your name**: 남에게 보일 이름
4. **Token**: `.env`에 넣은 그 문자열
5. **Connect**

첫 번째로 들어온 사람이 자동으로 **scribe**가 됩니다. scribe만 씬을 디스크에 저장합니다.
(여러 명이 동시에 `Ctrl+S`를 눌러 서로의 파일을 덮어쓰는 사고를 구조적으로 막기 위한 장치입니다.)

> 토큰은 `EditorPrefs`(사용자·PC 단위)에 저장되고, 프로젝트 폴더에는 저장되지 않습니다.
> `ProjectSettings/`에 평문으로 남기면 그게 그대로 Git에 커밋되기 때문입니다.

---

## 3. 지금 되는 것 / 아직 안 되는 것

정직하게 적습니다. 현재 릴리스는 **연결 계층**까지입니다.

| 기능 | 상태 |
|---|---|
| 서버 기동·`/health`·토큰 인증 | ✅ 동작 |
| 룸(room) 분리, 참여자 목록(presence) | ✅ 동작 |
| op 전송·권위 적용·브로드캐스트·중복 재전송 차단 | ✅ 동작 (`tools/SceneCollab.Smoke`로 실측) |
| Unity 패키지 설치·창·연결 | ✅ 동작 |
| 씬 오브젝트 변경 캡처(`ObjectChangeEvents`) | 🚧 다음 단계 |
| 캡처한 op를 남의 씬에 적용(`SerializedProperty`) | 🚧 다음 단계 |
| scribe 저장 가로채기, `*.unity.ids.json` 사이드카 | 🚧 다음 단계 |

즉 **"붙는 것"은 지금 되고, "씬이 서로 따라 움직이는 것"은 다음 커밋**입니다.
순서를 이렇게 잡은 이유는, 캡처·적용은 서버가 op를 정확히 한 번 적용한다는 보장 위에서만
의미가 있기 때문입니다. 그 보장을 먼저 테스트로 고정했습니다.

---

## 4. 잘 안 될 때

| 증상 | 확인 |
|---|---|
| `401` / 연결 즉시 끊김 | 토큰 오타. 서버 `.env`와 창의 Token이 같은지 |
| `protocol_mismatch` | 패키지와 서버 버전이 다름. 릴리스 태그를 맞추세요 |
| `ws://` 연결 실패, `wss://`는 됨 | 프록시에서 Websockets Support가 꺼져 있음 |
| 서버가 시작하자마자 종료 | `SCENECOLLAB_TOKEN` 미설정 (로그에 `Refusing to start`) |
| 참여자가 안 보임 | Room 이름이 다름. 대소문자까지 같아야 함 |
