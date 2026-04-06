# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 프로젝트 개요

`HiTessModelBuilder`는 C# .NET 8.0 라이브러리(`OutputType=Library`)로 빌드되는 **FE(Finite Element) 모델 자동 생성기**입니다. 구조/배관/장비 CSV 데이터를 파싱하여 Node, Element(1D Beam/Tube), Rigid(RBE2), PointMass를 생성하고, 힐링 파이프라인을 거쳐 Nastran BDF 파일로 내보냅니다. 외부 Launcher GUI에서 `MainApp.RunApplication(options)`를 직접 호출하는 방식으로 사용됩니다.

## 빌드 및 실행

```bash
# 빌드
dotnet build HiTessModelBuilder/HiTessModelBuilder.csproj

# 디버깅 실행 (PathManager.Current에 지정된 경로 사용)
dotnet run --project HiTessModelBuilder/HiTessModelBuilder.csproj

# CLI 실행 인자 형식 (Launcher에서 호출 시)
HiTessModelBuilder.exe --stru "C:\stru.csv" --pipe "C:\pipe.csv" --mesh 300 --verbose true --nastran false
```

**CLI 인자 목록:**
| 인자 | 기본값 | 설명 |
|------|--------|------|
| `--stru` | PathManager 값 | 구조 CSV 경로 (필수) |
| `--pipe` | PathManager 값 | 배관 CSV 경로 (옵션, `null` 가능) |
| `--equip` | PathManager 값 | 장비 CSV 경로 (옵션, `null` 가능) |
| `--mesh` / `--meshsize` | 500.0 | 메싱 크기 (mm) |
| `--nastran` | true | Nastran 검증 실행 여부 |
| `--verbose` | false | 상세 디버그 출력 |
| `--pipeline` | true | 파이프라인 스테이지 로그 |

**로컬 디버깅:** `PathManager.cs`의 `Current` 필드에서 테스트 케이스를 전환합니다 (`Case1`~`Case6`, `Samho_1`, `Mipo_1`, `MU_Test1`~`MU_Test5`).

## 아키텍처

### 핵심 데이터 구조
- **`FeModelContext`**: 모든 FE 엔티티의 루트 컨테이너. `Materials`, `Properties`, `Nodes`, `Elements`, `Rigids`, `PointMasses`, `WeldNodes`를 보유.
- **엔티티 불변성**: `Element`, `RigidInfo`, `PointMass` 등은 불변(Immutable) 객체. 수정 시 반드시 **기존 ID `Remove` → 새 객체 `AddWithID`** 패턴 사용.
- **컬렉션 순회 안전**: foreach 중 컬렉션 변경 시 `ToList()` 로 복사본 순회. 예: `foreach (var eid in elements.Keys.ToList())`
- **`RawCsvDesignData`**: CSV 파싱 결과를 담는 원시 DTO. `StructureEntity`, `PipeEntity`, `EquipEntity` 목록 포함.

### 실행 흐름
```
Program.cs (RunApplication)
  ├── [단계 1] FeModelLoader.LoadAndBuild()
  │     ├── CsvRawDataParser → RawCsvDesignData
  │     └── RawFeModelBuilder + PipeModelBuilder → FeModelContext
  ├── [단계 2] FeModelProcessPipeline.RunFocusingOn(targetStage=7)
  │     └── STAGE_0 ~ STAGE_7 누적 실행 (아래 파이프라인 스테이지 참조)
  ├── [단계 3] ElementMeshingModifier.Run() — 메시 크기 최적화
  ├── [단계 4] StructuralSanityInspector.Inspect() — SPC 노드 산출
  ├── [Phase 1] BdfExporter.Export() + NastranExecutionService.RunAndAnalyze() — 검증용 BDF
  └── [Phase 2] BdfExporter.Export() — 납품용 최종 BDF
```

### 파이프라인 스테이지 (`FeModelProcessPipeline`)
스테이지는 누적 방식(STAGE_N은 0~N 전부 실행). 수렴 루프가 있는 스테이지에 주의:

| Stage | 핵심 작업 |
|-------|-----------|
| 1 | 기존 노드에 의한 요소 분할 |
| 2 | +교차점 분할 |
| 3 | +단요소 붕괴, 직선 노드 병합 |
| 4 | **수렴 루프**: 자유단 노드 연장(`ExtendToIntersect`) → 재분할/재병합 (최대 10회) |
| 5 | **수렴 루프**: 강체 그룹 병진 이동 → Stage 4 내부 루프 중첩 (최대 10회×10회) |
| 6 | RBE 연결 생성 |
| 7 | U-Bolt 스냅 연결, BOX타입 처리, Rigid 통폐합, 근접 Rigid 병합, 고립 Rigid 스냅 |

### 성능 유틸리티 (`Pipeline/Utils/`)
- **`SpatialHash` / `ElementSpatialHash`**: 3D 공간 탐색. O(N²) 탐색 대신 반드시 사용.
- **`UnionFind`**: 그래프 연결성/연결 그룹 탐색에 사용.
- **`Point3dUtils`, `Vector3dUtils`, `ProjectionUtils`**: 기하 연산 전용 유틸. C# Math 클래스 대신 이 유틸 우선 사용.

### 로깅 시스템
- **`PipelineLogger`**: `DualTextWriter`로 `Console.Out`을 가로채 콘솔+파일 동시 출력. `using` 블록으로 감싸야 함.
- Modifier에 전달하는 `Action<string>? log`는 항상 `logger.LogDelegate`를 사용.
- 색상 컨벤션: 경고=Yellow, 에러=Red, 성공=Cyan/Green.
- `opt.PipelineDebug`: 스테이지 진입 배너 및 Modifier 통계 출력 여부. 타겟 스테이지에서만 활성화하여 과거 스테이지 로그 도배 방지.
- `opt.VerboseDebug`: 요소별 세부 처리 내역 출력. 타겟 스테이지에서만 활성화.

## AI 코드 작성 규칙

### 전체 코드 작성 원칙
- **`// ... 기존 코드 생략` 형태의 부분 출력 금지.** 파일 전체를 항상 완전한 소스로 작성.

### 불변 엔티티 수정 패턴
```csharp
// ❌ 잘못된 방법: 직접 속성 수정 불가 (불변 객체)
// ✅ 올바른 방법: Remove 후 새 객체로 AddWithID
context.Rigids.Remove(id);
context.Rigids.AddWithID(id, independentNodeId, dependentNodeIds, newCm, extraData);
```

### Modifier 구조 표준
모든 Modifier는 정적 클래스 + `Options` record + `Run()` 정적 메서드 패턴을 따릅니다:
```csharp
public static class MyModifier
{
    public record Options(double Tolerance, bool PipelineDebug, bool VerboseDebug);
    public static int Run(FeModelContext context, Options opt, Action<string>? log = null) { ... }
}
```

### BDF 출력 주의사항
- `BdfExporter.Export()` 두 번째 BDF 출력 전 `ToggleUboltRigidity()`로 U-Bolt 자유도 스위칭.
- Phase 1(검증용): `forceRigid=true` → 모든 U-Bolt `Cm="123456"`
- Phase 2(납품용): `forceRigid=opt.ForceUboltRigid` → 원본 `Rest` 값 복원

### 만료일 로직
`Program.cs`의 `RunApplication()`에 Time-Bomb 로직이 있음(`expirationDate` 변수). 베타 기간 연장 시 이 날짜만 수정.
