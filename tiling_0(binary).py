import open3d as o3d
import numpy as np
import os
import struct

# 입력 및 출력 경로 설정
input_dir = r"D:\3D\ply\redandblack\Ply\low4.0\ds4.0"
output_root = r"D:\3D\ply\redandblack\Ply\low4.0\low_til128"

# 프레임 범위
start_frame = 1450
end_frame = 1749  

for frame_idx in range(start_frame, end_frame + 1):
    file_path = os.path.join(input_dir, f"redandblack_ds_{frame_idx:04d}.ply")

    if not os.path.exists(file_path):
        print(f"파일 없음: {file_path}")
        continue
    output_dir = os.path.join(output_root, f"low_{frame_idx:04d}")
    os.makedirs(output_dir, exist_ok=True)

    # 포인트 클라우드 vktld
    pcd = o3d.io.read_point_cloud(file_path)
    points = np.asarray(pcd.points)
    colors = (np.asarray(pcd.colors) * 255).astype(np.uint8)

    if points.size == 0:
        print(f"frame {frame_idx:04d}: 포인트 없음")
        continue

    # bounding box 영역 설정
    fixed_origin = np.array([0.0, 0.0, 0.0])
    seq_size = np.array([1024.0, 1024.0, 1024.0])

    tile_size = 128  # 타일 크기

    in_bbox_mask = np.all(
        (points >= fixed_origin) & (points < (fixed_origin + seq_size)), axis=1
    )
    points = points[in_bbox_mask]
    colors = colors[in_bbox_mask]

    if points.size == 0:
        print(f"frame {frame_idx:04d}: bbox 안 포인트 없음")
        continue

    tile_indices = np.floor((points - fixed_origin) / tile_size).astype(int)
    unique_tiles = np.unique(tile_indices, axis=0)

    tile_count = 1

    for tile in unique_tiles:
        mask = np.all(tile_indices == tile, axis=1)
        tile_points = points[mask]
        tile_colors = colors[mask]

        if len(tile_points) == 0:
            continue

        # 타일 파일 경로
        tile_filename = f"tile_{tile_count}.ply"
        tile_path = os.path.join(output_dir, tile_filename)

        # 바이너리 PLY 헤더 작성
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

        # 저장
        with open(tile_path, 'wb') as f:
            f.write(binary_header.encode('utf-8'))
            for p, c in zip(tile_points, tile_colors):
                f.write(struct.pack('<fffBBB', p[0], p[1], p[2], c[0], c[1], c[2]))

        tile_count += 1

    print(f"frame {frame_idx:04d}: {tile_count - 1}개 타일 생성 완료 → {os.path.basename(output_dir)}")
