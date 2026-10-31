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

> 검증 상태: `docker compose up -d` 경로를 실측했습니다. 컨테이너 `healthy`, 호스트 포트의
> `/health` 응답, 프로토콜 프로브 26체크 PASS까지 확인했습니다. 이미지는 여전히 직접 받아
> `/health`·양방향 편집 전파도 검증했습니다. 문제가 생기면 `docker compose logs -f`로 먼저
> 확인하세요.
>
> `.env`의 `SCENECOLLAB_PORT`는 **호스트 포트**입니다. 컨테이너 안쪽 포트는 8080으로
> 고정되므로(`docker-compose.yml`), 포트를 바꿔도 이 값만 수정하면 됩니다.

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
"com.obsihill.scenecollab": "https://github.com/Obsihill/scene-collab.git?path=unity/com.obsihill.scenecollab#v0.4.1"
```

Unity가 패키지를 받아오면 메뉴에 **Tools > Scene Collab** 이 생깁니다.

### 접속 순서

1. **Address**: `ws://localhost:8080/ws` (또는 `wss://.../ws`)
2. **Token**: `.env`에 넣은 그 문자열
3. **Your name**: 남에게 보일 이름
4. **Refresh** — 지금 누군가 열어둔 씬 목록이 뜹니다
5. 목록에서 씬을 고르고 **Connect**

아직 아무도 씬을 열지 않았다면 **Room** 칸에 팀이 정한 이름을 적고 Connect 하세요.
**먼저 들어온 사람이 그 씬의 Host가 되고**, 이후 사람은 목록에서 그 씬을 골라 들어옵니다.

### Host 모델 (v0.3의 핵심)

```
Host  = 씬을 열고 있는 사람 = 디스크에 저장하는 유일한 사람(scribe)
Guest = 같은 씬을 받아서 함께 편집하는 사람
```

- **한 세션 = 한 씬.** 다른 씬을 열어둔 사람의 세션에는 들어갈 수 없습니다(씬 GUID가 다르면
  `scene_mismatch`로 거부). 남의 씬에 내 씬이 조용히 섞이는 사고를 막습니다.
- **저장은 Host만.** 다른 편집자가 `Ctrl+S`를 누르면 경고와 함께 막힙니다. 여러 명이 각자
  저장해서 서로의 파일을 덮어쓰는 사고를 구조적으로 없앱니다.
- **Host가 나가면 자동 승계.** 남아 있는 사람 중 **가장 오래 접속해 있던 사람**이 새 Host가
  되어 저장이 멈추지 않습니다.
- **구조 변경은 락을 잡고.** 오브젝트 추가/삭제/부모 변경/순서 변경/컴포넌트 추가·삭제는 락을 잡은 사람만
  수행하고, 대상 오브젝트에 락을 잡아야 실행됩니다. 다른 사람이 같은 오브젝트를 건드리면
  `locked`로 거부됩니다.

> 토큰은 `EditorPrefs`(사용자·PC 단위)에 저장되고, 프로젝트 폴더에는 저장되지 않습니다.
> `ProjectSettings/`에 평문으로 남기면 그게 그대로 Git에 커밋되기 때문입니다.

---

## 3. 동기화되는 것 / 안 되는 것

### 되는 것

| 기능 | 비고 |
|---|---|
| Transform 위치·회전·스케일 | 채널 단위(x/y/z 따로) |
| GameObject 이름, `activeSelf` | |
| 컴포넌트 `enabled` | 체크박스 |
| 컴포넌트 필드 — `int`, `float`, `bool`, `string`, `enum`, `Vector2`, `Vector3`, `Quaternion`, `Color` | 어떤 컴포넌트든 `SerializedProperty`를 훑어서 찾음 |
| 오브젝트 **생성** | Host + 락 필요. 상대 씬에 실제 GameObject가 생김 |
| 오브젝트 **삭제** | Host + 락 필요 |
| **부모 변경**(reparent), **자식 순서 변경** | Host + 락 필요 |
| **컴포넌트 추가** | 락 필요. 이 프로젝트에 없는 스크립트면 이름이 붙은 자홍색 대체 오브젝트가 남음 |
| **컴포넌트 삭제** | 락 필요. 이미 지워진 컴포넌트를 다시 지우는 op는 성공으로 처리(재접속 재생) |
| **프리팹 인스턴스 필드 오버라이드** | 값을 쓰고 `RecordPrefabInstancePropertyModifications`로 고정 |
| 씬 목록(`/rooms`)·씬 핸드셰이크 | 씬 이름·Host 이름·참가자 수 |
| 에셋 매니페스트·누락 감지 | Host가 GUID 목록을 내려주고 클라가 자기 쪽과 대조 |
| 오브젝트 락 | 전체 락 집합이 모든 참여자에게 브로드캐스트됨 |

### 안 되는 것 (지금은)

| 기능 | 이유 / 계획 |
|---|---|
| 프리팹 **인스턴스에 컴포넌트 추가·삭제** | 인스턴스의 `addedObjects` 목록은 전송하지 않음 (v0.5) |
| `m_Modifications` 배열 **순서 정규화** | 값과 오버라이드는 동기화되지만 배열 순서는 맞추지 않음 (Unity가 저장 시 재작성) |
| 프리팹 **에셋** 자체 편집 | 씬 오브젝트가 아니라서 범위 밖. 에셋은 Git으로 공유 |
| 오브젝트/에셋 **참조 필드**(머티리얼 슬롯, 프리팹 링크, 스크립트 참조 등) | 직렬화 타입 화이트리스트 밖 |
| `Vector4`, `Vector2Int`, `Vector3Int`, `Rect`, `RectInt`, `Bounds`, `BoundsInt`, `LayerMask` | 지원. 값 손실 없이 왕복 |
| 작은 스칼라 배열(최대 64개) — `float[]`, `int[]`, `string[]`, `Vector3[]`, `Color[]` 등 | 지원. 배열 하나가 값 하나로 이동하고, 개수 변경도 반영 |
| 64개 초과 배열·빈 배열 | 미지원. 비교할 대상이 없고 전송량 상한도 없음 |
| 오브젝트/에셋 참조 필드, `AnimationCurve`, `Gradient` | 미지원. 보내는 쪽에서 조용히 건너뜀 |
| `TerrainData`(높이맵·스플랫·나무·디테일) | 1024² 한 장이 4MB급 → v0.5에 스트로크 단위 영역 델타로 분리. 그 전까지 터레인은 Host만 편집 |
| 라이트맵 굽기·네비메시 베이크·터레인 LOD | 설계상 Host가 하고 저장 → Git 공유 |
| 씬 여러 개 동시 편집 / 씬 간 이동 | 한 세션 = 한 씬 |
| Undo / Redo | 각자 로컬 undo 스택. 상대의 undo는 상대에게만 |
| 선택·카메라·커서·인스펙터 포커스 | 모델링하지 않음 |
| 같은 값을 동시에 수정 | 마지막에 쓴 사람이 이김(LWW). 같은 오브젝트의 **구조**를 동시에 바꾸는 건 락으로 사전 차단 |

---

## 샘플 씬으로 먼저 확인하기

Package Manager → **Scene Collab** → *Samples* → **Quickstart Scene** → *Import* 후
`Assets/Samples/.../Quickstart/Quickstart.unity`를 열면 큐브 2개 + 라이트만 있는 씬이 나옵니다.
프로젝트 에셋이 섞이지 않아 세션 동작만 따로 확인할 때 편합니다.

## 4. 실측 근거

문서만 보고 "됩니다"라고 쓰지 않았습니다. 아래는 실제로 돌려서 통과한 항목입니다.

- `tools/SceneCollab.V03Probe` — 에디터 없이 **.NET WebSocket 피어 2개**로 실제 서버를 상대:
  씬 목록·401, Host/scribe 부여, 씬 GUID 불일치 거부, 구조 op 중계(부모·순서·컴포넌트 타입·
  프리팹 GUID 보존), 락 전체집합 브로드캐스트, 비-Host 구조 op 거부, 에셋 GUID 전달, Host 승계 —
  **23개 체크, 3회 연속 통과**.
- `LiveHarnessV03` — Unity가 아닌 피어가 **헤드리스 Unity 씬에 `Guest Cube`를 만들고 `(2,3,4)`로
  옮기고 삭제**했고, Unity 쪽이 세 가지를 모두 적용(오브젝트 존재·좌표·노드 id 결속·삭제 확인).
- `tools/live-run.sh` — 값 편집이 양방향으로 전파.
- 서버 단위 테스트 24개, Unity EditMode 테스트 36개 통과.

---

## 5. 잘 안 될 때

| 증상 | 확인 |
|---|---|
| `401` / 연결 즉시 끊김 | 토큰 오타. 서버 `.env`와 창의 Token이 같은지 |
| `protocol_mismatch` | 패키지와 서버 버전이 다름. 릴리스 태그를 맞추세요 |
| `scene_mismatch` | 다른 씬을 열어둔 세션에 들어가려 함. Host가 연 씬 목록에서 고르세요 |
| `locked` | 다른 사람이 그 오브젝트를 구조 편집 중. 끝나면 다시 시도 |
| `not_host` | 구조 변경은 Host만 가능. Host에게 요청하거나 Host 승계를 기다리세요 |
| `ws://` 연결 실패, `wss://`는 됨 | 프록시에서 Websockets Support가 꺼져 있음 |
| 서버가 시작하자마자 종료 | `SCENECOLLAB_TOKEN` 미설정 (로그에 `Refusing to start`) |
| 참여자가 안 보임 | Room 이름이 다름. 대소문자까지 같아야 함 |
| 내 저장이 막힘 | Host가 아님. 정상 동작입니다 |
