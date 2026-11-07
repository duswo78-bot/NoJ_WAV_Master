# NoJ WAV Master v1

FFmpeg 및 C# .NET 8.0 WPF 기반의 **원클릭 음원 마스터링 & 순수 오디오 렌더링 스튜디오** 애플리케이션입니다.

## 주요 기능

- **원클릭 마스터링 엔진 (4가지 모드)**
  - **Clean Master (`20Hz–18kHz · -1.0dB`)**: 20Hz 이하 초저역 및 18kHz 이상 비가청 대역 컷과 Brickwall Limiter 적용
  - **Loud (`-14 LUFS`)**: 스트리밍 플랫폼 표준 음압(`-14 LUFS`, `-1.0 dBTP`) 노멀라이징
  - **Manual FX**: 우측 확장형 `FX Rack`을 통한 세부 수동 마스터링
  - **Convert Only**: FX 바이패스 순수 포맷 변환 및 메타데이터 완전 초기화
- **수동 FX 스튜디오 랙 (`FX Rack`)**
  - **Edge EQ Filter**: Low Cut (`10~200 Hz`), High Cut (`10,000~22,000 Hz`)
  - **Brickwall Ceiling Limiter**: True Peak 천장 제한 (`-3.0 ~ -0.1 dB`)
  - **Loudness Normalizer**: 타겟 음압 (`-20 ~ -7 LUFS`)
  - **Analog Tube Warmth**: 배음 드라이브 (`0 ~ 100%`)
  - **Space Reverb & Room Echo**: 딜레이 (`20 ~ 300 ms`) 및 잔향 블렌딩 (`5 ~ 70%`)
  - **Stereo Image Widener**: 스테레오 이미지 확장 (`100 ~ 200%`)
  - **Pitch & Speed Shift**: 반음 단위 키/템포 조절 (`-6 ~ +6 st`)
- **Before / After 즉시 비교 미니 플레이어 & 실시간 파형**
  - 재생 위치 끊김 없이 원본(`BEFORE`)과 마스터링본(`AFTER`)을 즉시 A/B 비교
  - 실제 PCM 샘플 기반 듀얼 컬러(`#F472B6` / `#4ADE80`) 파형 렌더링 및 클릭 즉시 구간 점프(Seek)
  - `Re-Master All` 버튼으로 재드래그 없이 슬라이더 변경값 즉시 재렌더링
- **순수 오디오 내보내기 (6종 포맷)**
  - `WAV 24-bit`, `WAV 16-bit`, `FLAC`, `MP3 320k`, `AIFF`, `AAC (.m4a)` (44.1kHz Stereo)
  - 모든 메타데이터, 비오디오 스트림, 인코더 태그 완전 제거 (`-map_metadata -1 -fflags +bitexact`)
- **무설치 단독 실행 지원**
  - `.NET 8` 런타임이 내장된 단일 실행 파일(`NoJ_WAV_Master_v1.exe`) 제공
  - 로컬에 `ffmpeg.exe`가 없는 PC에서도 최초 변환 시 자동 다운로드 및 설치 지원 (오프라인 환경은 실행 파일과 같은 폴더에 `ffmpeg.exe` 동봉 시 자동 인식)

## 빌드 방법

```powershell
dotnet publish -c Release
```

빌드 완료 시 `bin/Release/net8.0-windows/win-x64/publish/NoJ_WAV_Master_v1.exe` 단일 파일이 생성됩니다.
