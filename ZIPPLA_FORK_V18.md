# ZipPlaFork-v18 변경 내역

- 베이스: `ZipPlaFork-v17` (v17-m1까지의 썸네일 병렬화 + 리뷰 지적 반영 전부 포함)
- 명명 규칙: 빌드마다 `ZipPlaFork-v18.1`, `v18.2`, `v18.3` … (폴더·zip·창 제목 공통)
- `VERSION` 파일이 현재 버전을 들고 있고, `Build-Fork.ps1`이 빌드→패키징→다음 패치로 자동 증가시킴.

## v18.1에서 손댄 것

v16 감사 지적 중 v17에 남아 있던 잔여분 + 버전 자동화:

1. **버전 자동화 (신규 요구)**
   - `VERSION` (예: `v18.1`) + `source\ZipPla\ForkVersion.cs` (`ForkVersion.Current`).
   - `Program.DisplayName` = `ZipPla v18.1` … 형태로 카탈로그 본창·뷰어·About에 표시.
     - 카탈로그: `CatalogForm.Constructor()`에서 `Text = Program.DisplayName`.
     - 뷰어: `ViewerForm.setTitleBar()`가 `Program.Name` 대신 `DisplayName` 사용.
     - About: 창 제목 + 로고 라벨에 `DisplayName`.
   - `Build-Fork.ps1`: VERSION 읽기 → ForkVersion 동기화 → MSBuild AutoBuild →
     `Desktop\coding\ZipPlaFork-v18.N\` + 동명 `.zip` 생성 → VERSION을 다음 패치로 증가.
     패키징은 exe·dll·language·문서만 포함 (config.xml·History.sor·ffmpeg·bin/obj 제외).
2. **`AlertError` 데드락 잔재 제거** (`Program.cs`)
   - `lock (StartForm)` + `Invoke` → 전용 `AlertErrorLock` + `BeginInvoke`.
   - v17까지도 남아 있던 구 Onginal 코드. 현재 호출이 한 곳이라 발현은 이론적이었으나 정석대로 수정.
3. 그 외 v16 감사 항목(① NGEN 파싱, ② NGEN/단일창 순서, ③ drawFileImage return,
   ④ 파이프 세션 스코프·ACL·타임아웃, ⑤ 절대경로화·폴백, ⑥ 클립보드 영속,
   ⑦ 압축해제 상한, UI스레드 분리·캐시, 200자 표시, 4.8·PostBuild 제거,
   i18n·AI키·컨텍스트메뉴)은 v17-d1에서 이미 반영되어 있어 v18에서 손대지 않음.

## 검사

- `Build-Fork.ps1` AutoBuild 빌드 오류 0 기준.
- `ZipPla.exe -selftest` 로그를 `ZipPlaFork-v18-1-selftest.log`로 보관.
