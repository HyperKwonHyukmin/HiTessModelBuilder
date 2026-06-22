# HiTessModelBuilder (`cmb`)

AM(Aveva Marine) 3D 설계 CSV → 1D Beam FEM → Nastran BDF 변환 CLI.

이 문서는 **HiTess WorkBench**가 `cmb` CLI를 호출할 때 필요한 정보(서브커맨드, 옵션, 입출력 위치, exit code, 산출물 구조)를 단일 진실원으로 제공한다.

- 런타임: .NET 8, Windows 단일 바이너리 또는 `dotnet run --project src/Cmb.Cli`
- 단위: **mm** (모든 좌표/길이/tolerance)
- 인코딩: 입력/출력 모두 UTF-8

---

## 0. 호출 패턴 요약

```
cmb <subcommand> [options]
```

| 서브커맨드 | 용도 | WorkBench 단계 |
|------------|------|----------------|
| `parse` | CSV 파싱 검증 (raw JSON 덤프) | (디버깅) |
| `build-raw` | 알고리즘 미적용 원본 모델 + BDF | Stage 1 사전 검증 |
| `build-full` | 전체 파이프라인 + phase JSON + 최종 BDF | **Stage 2 진입 시 호출** |
| `apply-edit-intent` | viewer가 만든 `*_edit.json` 적용 | Stage 2 사용자 편집 후 |

루트 레벨 단축형도 지원한다 (서브커맨드 없이):
```
cmb --apply-edit-intent <folder> [--out <dir>] [--strict]
```

---

## 1. 출력 폴더 규칙 (모든 서브커맨드 공통)

`parse` / `build-raw` / `build-full` 은 **CSV가 위치한 디렉터리 안에 타임스탬프 서브폴더를 자동 생성**하고, 그 안에 모든 산출물을 쓴다.

```
{csvDir}/{yyyyMMdd_HHmmss}/
```

- 형식: `DateTime.Now.ToString("yyyyMMdd_HHmmss")` (로컬 시간, 예: `20260430_134205`)
- `--input <DIR>` 사용 시 → `<DIR>/{timestamp}/`
- `--stru/--pipe/--equip` 중 처음 지정한 파일이 위치한 폴더가 기준
- WorkBench 권장 입력 경로: `userConnection/<user>/<project>/csv/`
  → 산출물은 자동으로 `userConnection/<user>/<project>/csv/{timestamp}/` 에 떨어짐
- **이 timestamp 폴더 경로는 stdout 첫 줄의 `출력 폴더: ...` 또는 `폴더: ...` 로 출력됨** → WorkBench는 이 경로를 캡처해 viewer IPC payload `jsonPath` 에 넘기면 됨
- 동일 timestamp 충돌 시 `Directory.CreateDirectory`가 idempotent → 덮어쓰기 가능 (1초 미만 재실행은 피할 것)

`apply-edit-intent` 만 다른 규칙: 기본 출력은 `<folder>/edited/`, `--out <DIR>` 으로 override 가능.

---

## 2. 공통 CSV 입력 옵션

`parse`, `build-raw`, `build-full` 세 커맨드 모두 동일한 입력 옵션을 받는다. **둘 중 하나를 반드시 지정**:

| 옵션 | 형식 | 설명 |
|------|------|------|
| `--input <DIR>` | 폴더 경로 | 폴더 안의 `*.csv` 파일 중 이름에 `struData`, `pipeData`, `equpData` (또는 `structure`/`support`/`supt`/`pipe`/`equip`) 토큰을 포함하는 파일을 자동 매칭 |
| `--stru <FILE>` | 파일 경로 | Structure CSV 명시 |
| `--pipe <FILE>` | 파일 경로 | Pipe CSV 명시 |
| `--equip <FILE>` | 파일 경로 | Equipment CSV 명시 |

규칙:
- `--input` 과 명시 옵션을 동시에 주면 → **명시 옵션이 우선**, `--input` 무시
- 3종 중 일부만 지정 가능 (예: pipe만 변환). 누락된 종은 빈 컬렉션으로 처리
- 셋 중 하나도 없으면 exit 1, 메시지: `[오류] --input 폴더 또는 --stru/--pipe/--equip 파일 중 하나 이상을 지정하세요.`
- 폴더 미존재 시 exit 1, 메시지: `[오류] 입력 폴더를 찾을 수 없습니다: <path>`

**최종 산출물 파일명 (designName)**: `--input` 모드에서는 입력 CSV 파일명에서 공통 prefix를 추출하고, 명시 모드에서는 첫 번째 CSV 파일명에서 `struData`/`structure`/`support`/`supt`/`pipeData`/`pipe`/`equpData`/`equip` 토큰을 제거한 결과.
예) `3468-35030-struData-A453412_20260108.csv` → `3468-35030-A453412_20260108.bdf`

---

## 3. `parse` — CSV 파싱 검증

```
cmb parse [--input <DIR> | --stru/--pipe/--equip <FILE>...]
```

CSV를 파싱한 결과만 raw JSON으로 덤프. 모델 빌드/알고리즘은 실행하지 않음.

**산출물**:
```
{csvDir}/{timestamp}/
  └─ {baseName}.raw.json
```

**stdout**:
```
완료: 구조물 N개  배관 M개  장비 K개
출력: <full path to .raw.json>
```

**stderr**: 파싱 스킵된 행마다 `[SKIP] <Kind> line <N> '<name>': <reason>`

**exit code**: 0 (성공), 1 (입력 오류). 파싱 스킵이 있어도 exit 0 (경고만).

WorkBench 사용 권장도: **낮음** (디버깅/사전 검증용).

---

## 4. `build-raw` — 알고리즘 미적용 원본 모델

```
cmb build-raw [--input <DIR> | --stru/--pipe/--equip <FILE>...]
```

CSV → FeModel 변환만 수행. Meshing/Intersection/RBE 등 모든 파이프라인 스테이지를 건너뛴다.

**산출물**:
```
{csvDir}/{timestamp}/
  ├─ {baseName}.initial.json   (phase="B", stageName="initial")
  └─ {baseName}.initial.bdf
```

**stdout**:
```
완료: 노드 N개  빔 M개  단면 K개  집중질량 P개
폴더: <timestamp dir>
JSON: <full path>
BDF:  <full path>
```

**exit code**: 0/1.

WorkBench 사용 권장도: **선택적** (Stage 1 결과 BDF 미리보기가 필요할 때만).

---

## 5. `build-full` — 전체 파이프라인 (★ Stage 2 메인 호출)

```
cmb build-full [csv-options] [--stopat <STAGE>] [--verbose-dump] \
               [--run-nastran] [--nastran-path <PATH>] [--leg-z-tol <MM>] \
               [--ubolt-full-fix] \
               [--mesh-size <MM>] [--mesh-size-structure <MM>] [--mesh-size-pipe <MM>]
```

### 5.1 옵션 상세

| 옵션 | 타입 | 기본값 | 동작 |
|------|------|--------|------|
| `--stopat <STAGE>` | string | (없음) | 지정한 sub-stage **이후** 실행 중단. 값은 sub-stage 이름 (예: `NodeEquivalence`, `Meshing`, `FinalValidation`). phase 이름이 아닌 sub-stage 이름임에 유의. 매칭은 case-insensitive. 중단 시 stdout: `--stopat 'X' 에서 중단.` |
| `--verbose-dump` | bool | false | `{outDir}/detail/{idx:D2}_{stageName}.{json,bdf}` 로 **모든 21개 sub-stage** 산출물 추가 dump. 디버깅용. 생성량: 21 × (JSON+BDF) ≈ 50~100MB (모델 규모 따라). |
| `--run-nastran` | bool | false | 최종 BDF에 GRAV(-9800) + leg 하부 SPC1을 추가하고 Nastran 실행. 미지정 시 BDF는 작성하되 SPC1은 Boundary 태그 노드만 사용 (해석은 안 함). |
| `--nastran-path <PATH>` | string | `C:\MSC.Software\MSC_Nastran\20131\bin\nastran.exe` | Nastran 실행 파일 경로. `--run-nastran` 동반 시에만 유효. |
| `--leg-z-tol <MM>` | double | `500.0` | leg 하부 SPC 검출 허용 Z 범위. 모델 최저 Z + 이 값 안에 있는 leg 하단 노드를 SPC seed로 사용. `--run-nastran` 동반 시에만 의미 있음. |
| `--ubolt-full-fix` | bool | false | 모든 UBOLT RBE2 (BOX + pipe)의 cm을 `"123456"` 으로 강제. mechanism 회피용. **최종 BDF/Final JSON 에만 반영**, phase JSON은 원본 cm 보존. |
| `--mesh-size <MM>` | double? | (없음) | Structure/Pipe 양쪽 최종 mesh 최대 길이. `FinalMeshSizeStage`가 모든 segment를 이 값 미만으로 분할. 양수만 허용. |
| `--mesh-size-structure <MM>` | double? | (없음) | Structure 전용. `--mesh-size`보다 우선. |
| `--mesh-size-pipe <MM>` | double? | (없음) | Pipe 전용. `--mesh-size`보다 우선. |

값 우선순위 (`--mesh-size-*`):
- Structure 최종값 = `--mesh-size-structure` ?? `--mesh-size` ?? null(분할 안 함)
- Pipe 최종값 = `--mesh-size-pipe` ?? `--mesh-size` ?? null

`--mesh-size*` 값이 0 이하면 exit 1: `[오류] mesh-size 값은 양수여야 합니다.`

### 5.2 산출물 (★ viewer가 읽는 파일들)

```
{csvDir}/{timestamp}/
  ├─ 00_InputAudit.json          입력 CSV 행 단위 감사 (mappingConfidence 등)
  ├─ 00_StageSummary.json        phase별 메트릭 + 모델 mass/CG
  ├─ 01_Preprocess.{json,bdf}    Preprocess phase 완료 시점
  ├─ 02_ElementSplit.{json,bdf}  ElementSplit phase 완료 시점
  ├─ 03_NodeMerge.{json,bdf}     NodeMerge phase 완료 시점
  ├─ 04_Connectivity.{json,bdf}  Connectivity phase 완료 시점
  ├─ 05_RigidPostProc.{json,bdf} RigidPostProc phase 완료 시점
  ├─ 06_Validation.{json,bdf}    Validation phase 완료 시점 (= 파이프라인 끝)
  ├─ {designName}.json           최종 모델 JSON (stageName="Final", stageIndex=99)
  ├─ {designName}.bdf            ★ 사용자에게 다운로드 제공할 최종 BDF
  └─ detail/                     (--verbose-dump 시에만 존재)
      ├─ 01_SanityPreprocess.{json,bdf}
      ├─ 02_Meshing.{json,bdf}
      ├─ ... (21개 sub-stage 전체)
      └─ 21_FinalValidation.{json,bdf}
```

`--run-nastran` 추가 시: 최종 BDF가 위치한 폴더에 Nastran 출력 (`*.f06`, `*.op2`, `*.log` 등)이 같이 생성됨.

JSON 스키마: `meta.schemaVersion = "1.2"`, `meta.unit = "mm"`. 자세한 필드는 `docs/json-schema.md` 참조.

### 5.3 Phase ↔ sub-stage 매핑

총 21개 sub-stage가 6개 phase로 묶여 phase 경계에서 dump된다.

| Phase 파일 (idx) | 실행되는 sub-stage 순서 |
|------------------|-------------------------|
| `01_Preprocess` | SanityPreprocess → **Meshing** |
| `02_ElementSplit` | NodeEquivalence → Intersection → **DanglingShortRemove** |
| `03_NodeMerge` | **CollinearNodeMerge** |
| `04_Connectivity` | **ConnectivityConvergence** (Extend/Cleanup/GroupConnect 수렴 루프) |
| `05_RigidPostProc` | PointMassReattach → BoxUboltConnection → SplitByExistingNodes → RigidProximityMerge → FreeEndRbe → **SplitByExistingNodes** |
| `06_Validation` | PreservedFreeEndCleanup → UboltAnchorVerticalize → OrphanNodeRemove → FinalMeshSize → FinalMeshProximityConnect → SplitByExistingNodes → FinalTopologyHealing → **FinalValidation** |

`--stopat` 값으로 인정되는 이름은 위 sub-stage 이름들 (예: `--stopat NodeEquivalence`).

### 5.4 stdout 형식

```
출력 폴더: <full path to timestamp dir>     ← WorkBench가 캡처할 라인
final mesh size: Structure=2000mm, Pipe=1000mm   (mesh-size 옵션 사용 시)
입력 감사: <path>/00_InputAudit.json
  [01/21] SanityPreprocess          Δelem=+0   Δnode=+0    12ms
  [02/21] Meshing                   Δelem=+45  Δnode=+90   234ms
  ╔══ [01] Phase: Preprocess
  ╟── [연결성] 그룹 N개 | 최대 X요소 (P%) | 고립노드 K개
  ╟── [건전성] 자유단 ... · 고립 ... · ...
  ╚══→ <path>/01_Preprocess.json
  ... (각 sub-stage + phase 경계)
SPC1: leg-하부 N개 + Boundary 태그, RBE2 dep 제외 K개, group 커버리지 보강 M개 → 최종 X개 노드 (123456)
BDF:  <path>/{designName}.bdf
JSON: <path>/{designName}.json
Mass: 12345.6 kg (beam 8000.0 + point 4345.6), CG=(X.X, Y.Y, Z.Z) mm
UBOLT cm → "123456" 강제 적용 ...    (--ubolt-full-fix 시)
SPC 태그: N개 노드에 NodeTags.Spc 부여, phase/detail JSON M개 갱신
파이프라인 완료.
```

### 5.5 exit code

| code | 의미 |
|------|------|
| 0 | 성공 (Diagnostic warning은 허용) |
| 1 | 입력 오류 또는 파이프라인 미완료 (`report.Succeeded == false`) |
| 2 | 파이프라인 완료했으나 Diagnostic Error 1개 이상 발생 |

WorkBench는 **0 또는 2** 모두 산출물이 작성된 상태로 간주하고 viewer를 띄울 수 있다 (Validation phase의 진단을 화면에서 보여주는 것이 viewer의 역할). exit 1만 “산출물 없음”.

`00_StageSummary.json` 의 `summary.totalErrors` 는 **최종 Validation phase의 Error 수**를 의미한다. 중간 stage의 Error는 후속 healing으로 복구될 수 있으므로, WorkBench의 최종 실패/성공 판정은 `summary.totalErrors` 또는 `06_Validation`/Final JSON의 diagnostic을 기준으로 한다. 예: `02_ElementSplit` 의 `SHORT_ELEMENT` Error가 남아 있어도 최종 Validation Error가 0이면 viewer는 성공 모델로 열 수 있다.

### 5.6 자동 복구/진단 코드

WorkBench는 아래 진단을 사용자에게 warning/error로 표시하되, 자동 복구된 warning은 실패로 처리하지 않는다.

| 코드 | Severity | 의미 | WorkBench 처리 |
|------|----------|------|----------------|
| `RBE2_CIRCULAR_DEPENDENCY_REMOVED` | Warning | RBE2 의존성이 순환되어 Nastran `USER FATAL MESSAGE 6202 (MCE1)` 를 유발할 수 있어, cycle 안의 RBE2 중 가장 큰 ID 1개를 자동 삭제함 | 실패 아님. trace/diagnostic에 표시 |
| `RBE2_CIRCULAR_DEPENDENCY` | Error | 자동 제거 후에도 RBE2 순환 의존성이 남아 있음 | 실패로 표시 |
| `MISSING_NODE` | Error | 요소 또는 RBE가 존재하지 않는 node를 참조함 | 실패로 표시 |
| `SHORT_ELEMENT` | Error | 중간 split/healing 단계에서 발견된 짧은 요소. 후속 stage에서 삭제/복구될 수 있음 | 최종 Validation에 남을 때만 실패로 표시 |

`RBE2_CIRCULAR_DEPENDENCY_REMOVED` 는 RBE2 reciprocal pair 또는 더 긴 cycle을 자동으로 끊기 위한 최종 방어 로직이다. 삭제 정책은 cycle에 포함된 RBE2 중 **ID가 가장 큰 RBE2 삭제**이다.

---

## 6. `apply-edit-intent` — viewer 편집 결과 적용

viewer가 작성한 `*_edit.json` 을 base phase JSON에 적용해 수정된 BDF/JSON을 생성한다.

```
cmb apply-edit-intent <folder> [--out <DIR>] [--strict]
```

또는 루트 단축형:
```
cmb --apply-edit-intent <folder> [--out <DIR>] [--strict]
```

| 인자/옵션 | 설명 |
|-----------|------|
| `<folder>` (필수) | viewer가 `<baseName>_edit.json` 을 저장한 폴더. 일반적으로 `build-full` 출력 timestamp 폴더. |
| `--out <DIR>` | 출력 폴더. 기본값: `<folder>/edited/`. 이미 존재 시 **삭제 후 재작성**. |
| `--strict` | viewer가 `validation.status="warning"` 으로 표시한 intent도 error로 처리해 거부. |

**동작**:
1. `<folder>` 안의 `*_edit.json` 중 가장 최신 1개를 선택 (여러 개면 stderr에 경고)
2. 동일 이름의 base `<baseName>.json` (와 가능하면 `.bdf`)을 로드
3. `stageRef.phase` / `stageRef.stageName` 이 base meta와 일치하는지 검증
4. 각 intent를 `deleteGroup` 또는 `addRigid` 로 적용
5. `<outDir>/<baseName>.json`, `<outDir>/<baseName>.bdf`, `<outDir>/apply-trace.json` 작성

**stdout**: `N intents applied, output at <outDir>`

**exit code**:
| code | 의미 |
|------|------|
| 0 | 적용 성공 |
| 2 | `*_edit.json` 없음 또는 intents 비어있음 |
| 64 | 입력 오류 (folder 미존재, JSON 파싱 실패, schemaVersion 불일치, base JSON 없음, stageRef mismatch 등) |
| 65 | intent 자체 거부 (viewer error status, `--strict` 하 warning, 모델 검증 실패) |
| 70 | 적용 도중 예외 — 출력 없음, temp dir만 정리 |

---

## 7. WorkBench 호출 예시

### Stage 2 진입 시 build-full 호출
```bash
cmb build-full --input "userConnection/u123/proj-A/csv" --ubolt-full-fix --mesh-size 2000
```
산출물: `userConnection/u123/proj-A/csv/20260430_134205/`
viewer IPC payload:
```json
{
  "jsonPath": "userConnection/u123/proj-A/csv/20260430_134205/06_Validation.json",
  "connectivityPath": null,
  "mode": "healed"
}
```

### 사용자 편집 후 적용
```bash
cmb apply-edit-intent "userConnection/u123/proj-A/csv/20260430_134205"
```
산출물: `userConnection/u123/proj-A/csv/20260430_134205/edited/`

### 디버깅 모드 (전체 sub-stage dump)
```bash
cmb build-full --input "..." --verbose-dump --stopat NodeEquivalence
```

### Nastran 자동 실행
```bash
cmb build-full --input "..." --run-nastran --nastran-path "C:\MSC.Software\MSC_Nastran\20131\bin\nastran.exe"
```

---

## 8. WorkBench 측 통합 체크리스트

- [ ] CSV 업로드 폴더(`userConnection/<user>/<project>/csv/`)에 CSV 배치. Structure는 `*struData*`, `*structure*`, `*support*`, `*supt*`; Pipe는 `*pipeData*`, `*pipe*`; Equipment는 `*equpData*`, `*equip*` 이름을 자동 인식
- [ ] `cmb build-full --input <csvFolder>` 호출, **stdout 첫 줄 `출력 폴더: ...`** 또는 **`폴더: ...`** 캡처
- [ ] timestamp 폴더 = viewer에 넘길 `jsonPath` 의 부모 디렉터리
- [ ] viewer에는 `06_Validation.json` 또는 `{designName}.json` 중 하나를 메인 진입점으로 전달
- [ ] viewer는 `list-dir-csvs` IPC로 폴더 안 phase JSON 전부를 자동 수집
- [ ] exit code 0 또는 2 → viewer 오픈, 1 → 에러 표시. 최종 실패 판정은 `00_StageSummary.json` 의 `summary.totalErrors` 또는 Final/`06_Validation` diagnostic 기준
- [ ] 중간 stage Error는 화면에 참고 정보로 표시 가능하지만, 후속 healing 후 최종 Validation Error가 0이면 실패로 처리하지 않음
- [ ] `RBE2_CIRCULAR_DEPENDENCY_REMOVED` warning은 자동 복구 내역으로 표시하고 실패로 처리하지 않음
- [ ] 사용자 "편집 적용" 버튼 → `cmb apply-edit-intent <timestampFolder>` 호출
- [ ] (선택) timestamp 누적 정리 정책 — 최근 N개만 유지 등

---

## 9. BDF만 입력으로 받는 외부 뷰어용 — Structure / Pipe 분리 규칙

`cmb` 가 출력하는 BDF는 **PID 범위**로 Structure / Pipe 카테고리를 구분한다. 외부 뷰어는 BDF만 가지고도 결정적으로 분류할 수 있다.

### 9.1 분류 규칙

| 카테고리 | PID 범위 |
|----------|----------|
| Structure | `1 ~ PipePidBase - 1` |
| Pipe | `PipePidBase` 이상 |

`PipePidBase` 기본값은 **1000**. `RunOptions.PipePidBase`로 변경 가능.

Equipment(`EntityCategory.Equipment`)는 PBEAML을 생성하지 않으므로 PID 분류 대상이 아니다 (RBE2 + CONM2만 출력됨).

### 9.2 BDF 안의 marker 주석

`BdfWriter`는 PBEAML 블록 앞에 다음 marker 주석을 자동 삽입한다 — Nastran은 `$` 라인을 무시하므로 해석에 영향 없음.

```
$ HiTessModelBuilder: PipePidBase=1000
$ ---- STRUCTURE SECTIONS (PID < 1000) ----
PBEAML  1       1               H
+       0.05    0.01    0.05    0.01    0.0
PBEAML  2       1               L
...
$ ---- PIPE SECTIONS (PID >= 1000) ----
PBEAML  1000    2               TUBE
+       0.1     0.09    0.0
...
```

### 9.3 뷰어 분류 의사코드

```text
1) BDF 본문을 라인 스캔
2) 정규식으로 marker 검색:
       ^\$\s*HiTessModelBuilder:\s*PipePidBase=(\d+)
   - 매칭됨 → 캡처값을 pipePidBase로 사용
   - 매칭 안 됨 → pipePidBase = 1000 (기본값 fallback)
3) PBEAML 카드를 모두 수집해 PID -> {Kind, Dims, MaterialId} 맵 작성
4) CBEAM 카드 각각에 대해 PID 조회:
       PID >= pipePidBase  -> Pipe
       PID <  pipePidBase  -> Structure
5) 카테고리에 따라 색상/그룹 분리하여 렌더링
```

### 9.4 marker가 없거나 손상된 경우 (fallback 휴리스틱)

레거시 BDF나 마커가 잘려나간 경우, 다음 보조 신호를 조합해 추정한다 (확정적이지는 않음):

- **PBEAML `TYPE=TUBE` + MAT1 RHO ≠ 7.85e-9** → Pipe 가능성 높음. (Pipe 단면은 유체 등가 밀도를 사용해 RHO가 가변, Structure는 기본 강 7.85e-9를 사용)
- **PBEAML `TYPE in {H, L, BOX, CHAN, BAR, ROD}`** → Structure 가능성 높음. (Pipe는 항상 TUBE)
- **MAT1 이름이 `Steel_Fluid_R<…>x<…>` 패턴** → Pipe (단, 외부 뷰어는 MAT1 카드의 이름을 직접 보지 못할 수 있음 — `cmb` 는 MAT1에 이름을 쓰지 않으므로 이 신호는 JSON에서만 유효)

가장 신뢰도 높은 fallback은 **TUBE + RHO 비교**. 그래도 모호하면 PID 1000 미만 = Structure 라는 단순 임계값으로 회귀하는 것이 충분하다.

### 9.5 PBEAML 출력 순서 보장

`cmb` 는 항상 **Structure 먼저 → Pipe 나중** 순으로 PBEAML을 출력한다. CBEAM 카드 자체의 출력 순서는 element ID 순으로 섞여 있을 수 있으므로, 뷰어는 반드시 PID 매핑을 먼저 구축한 뒤 CBEAM을 분류해야 한다.

### 9.6 한계

- `PipePidBase`를 비표준값(예: 5000)으로 빌드한 BDF는 marker 주석이 없으면 정확히 분류할 수 없다 — 그래서 marker 주석을 BDF 본문에 항상 함께 출력한다.
- Equipment의 RBE2 (UBOLT, EQUIPMENT 등)와 CONM2는 BDF에서 종류를 구분할 수 없다. 별도의 카테고리 표시가 필요하면 향후 별도 marker 컨벤션을 추가할 예정.

---

## 10. 빌드 / 단독 실행 (참고)

```bash
# 솔루션 빌드
dotnet build HiTessModelBuilder.sln -c Release

# 개발 중 직접 실행
dotnet run --project src/Cmb.Cli -- build-full --input samples/hitess_mini

# 배포 바이너리 (단일 exe)
dotnet publish src/Cmb.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# → bin/Release/net8.0/win-x64/publish/Cmb.Cli.exe (cmb.exe로 rename 가능)
```

WorkBench는 `cmb.exe` 단일 파일을 vendoring 후 `child_process.execFile` 로 호출하면 됨.
