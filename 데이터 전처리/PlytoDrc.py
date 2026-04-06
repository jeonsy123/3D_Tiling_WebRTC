import os
import subprocess
from pathlib import Path

# 경로 설정
encoder = r"D:\Draco\draco\build\Release\draco_encoder.exe"
in_root = Path(r"D:\3D\ply\redandblack\Ply\low16.0\low_tile512")
out_root = Path(r"D:\3D\ply\redandblack\Drc\low16.0\draco_low_tile512")

# 프레임 범위
start_id = 1450
end_id   = 1749   

# Draco 옵션
cl = 6     # 압축 레벨
qp = 16    # 양자화 비트

# 입력 경로 확인
if not Path(encoder).is_file():
    raise FileNotFoundError(f"인코더 경로가 없습니다: {encoder}")
if not in_root.exists():
    raise FileNotFoundError(f"입력 폴더가 없습니다: {in_root}")

out_root.mkdir(parents=True, exist_ok=True)

ok = 0
fail = 0
skip = 0

for f in range(start_id, end_id + 1):  
    f4 = f"{f:04d}" 

    in_folder = in_root / f"low_{f4}"
    if not in_folder.exists():
        print(f"입력 폴더 없음: {in_folder}")
        continue

    out_folder = out_root / f"low_{f4}"
    out_folder.mkdir(parents=True, exist_ok=True)

    for ply_file in in_folder.glob("*.ply"):
        src = str(ply_file)
        dst = str(out_folder / (ply_file.stem + ".drc"))

        if os.path.exists(dst):
            print(f"[SKIP] {src} -> {dst} (exists)")
            skip += 1
            continue

        # 인코더 실행
        cmd = [encoder, "-i", src, "-o", dst, "-cl", str(cl), "-qp", str(qp)]
        result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

        if result.returncode == 0:
            print(f"{src} -> {dst}")
            ok += 1
        else:
            print(f"{src} (code={result.returncode})")
            print(result.stderr.strip())
            fail += 1

# 요약
print("\n=== SUMMARY ===")
print(f"OK   : {ok}")
print(f"FAIL : {fail}")
