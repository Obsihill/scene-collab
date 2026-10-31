# Quickstart scene / 퀵스타트 씬

협업 기능만 확인하는 최소 씬입니다. 큐브 2개 + 디렉셔널 라이트 + 카메라뿐이고, 특정 프로젝트의
에셋·프리팹·스크립트는 하나도 들어 있지 않습니다.

## 쓰는 법

1. Package Manager에서 이 패키지의 **Samples** 탭 → **Quickstart Scene** → **Import**.
2. `Assets/Samples/Obsihill Scene Collab/*/Quickstart/Quickstart.unity`를 엽니다.
3. Host가 될 사람이 **Tools → Scene Collab** 창에서 서버 URL(`ws://127.0.0.1:8080/ws`)과 토큰을
   넣고 **Connect**. 이 사람이 씬을 소유합니다(저장 담당).
4. 나머지는 같은 창에서 새로고침 → 목록에 뜬 이 씬을 골라 접속.
5. 아무 오브젝트나 옮겨 보세요. Host가 저장하면 `Quickstart.unity.ids.json` 사이드카가 함께
   커밋되어야 다음 사람의 오브젝트 식별자가 맞습니다.

## Notes

- 씬은 **Host만 저장**합니다. 다른 편집자의 `Ctrl+S`는 저장 가드가 막습니다.
- 구조 변경(오브젝트 추가/삭제/부모 변경/컴포넌트 추가·삭제)은 **락을 잡은 Host만** 합니다.
  값 편집(Transform·컴포넌트 필드·배열)은 누구나 할 수 있고, 같은 값을 동시에 고치면 마지막 쪽이
  이깁니다.
- **Host mode with no server** is the zero-setup path: tick *Host mode* in the window and connect to
  `ws://localhost:8080/ws` on your own machine.
