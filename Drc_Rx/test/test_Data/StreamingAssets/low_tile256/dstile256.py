import open3d as o3d
import numpy as np
import os
import struct

# ✅ 입력 및 출력 경로 설정
input_dir = r"D:\3D\ply\redandblack\Ply\ds"
output_root = r"D:\3D\ply\redandblack\Ply\ds\low_tile256"

# ✅ 1450 ~ 1749 총 300개 프레임 순회
for frame_idx in range(1450, 1750):
    file_path = os.path.join(input_dir, f"redandblack_ds_{frame_idx}.ply")
    output_dir = os.path.join(output_root, f"low_{frame_idx}")
    os.makedirs(output_dir, exist_ok=True)

    # 포인트 클라우드 파일 열기
    pcd = o3d.io.read_point_cloud(file_path)

    # 포인트 및 색상 정보 가져오기
    points = np.asarray(pcd.points)
    colors = (np.asarray(pcd.colors) * 255).astype(np.uint8)  # 0~1 → 0~255로 변환

    if points.size == 0:
        print(f"[⚠️ 경고] frame {frame_idx}: 포인트 없음, 건너뜀")
        continue

    # ✅ 고정된 시퀀스 바운딩 박스 설정
    fixed_origin = np.array([0.0, 0.0, 0.0])  # seqOrigin
    seq_size = np.array([1024.0, 1024.0, 1024.0])  # seqSizeWhd

    # 🔧 타일 크기
    tile_size = 256

    # ⚠️ 바운딩 박스 범위 내의 점만 선택
    in_bbox_mask = np.all((points >= fixed_origin) & (points < (fixed_origin + seq_size)), axis=1)
    points = points[in_bbox_mask]
    colors = colors[in_bbox_mask]

    # 🎯 타일 인덱스 계산
    tile_indices = np.floor((points - fixed_origin) / tile_size).astype(int)
    unique_tiles = np.unique(tile_indices, axis=0)

    tile_count = 1
    for tile in unique_tiles:
        mask = np.all(tile_indices == tile, axis=1)
        tile_points = points[mask]
        tile_colors = colors[mask]

        if len(tile_points) == 0:
            continue

        # 바이너리 PLY 저장 경로
        tile_filename = f"tile_{tile_count}.ply"
        tile_path = os.path.join(output_dir, tile_filename)

        # 바이너리 헤더 작성
        binary_header = f"""ply
format binary_little_endian 1.0
element vertex {len(tile_points)}
property float x
property float y
property float z
property uchar red
property uchar green
property uchar blue
end_header
"""

        with open(tile_path, 'wb') as f:
            f.write(binary_header.encode('utf-8'))
            for p, c in zip(tile_points, tile_colors):
                f.write(struct.pack('<fffBBB', p[0], p[1], p[2], c[0], c[1], c[2]))

        tile_count += 1

    print(f"[✅ 완료] frame {frame_idx}: {tile_count - 1}개 타일 low_{frame_idx}에 저장됨")
