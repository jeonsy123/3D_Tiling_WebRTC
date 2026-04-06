import open3d as o3d
import numpy as np
import os
import struct

# 입력 경로 설정
base_dir = r"D:\3D\ply\redandblack\Ply"
output_dir = os.path.join(base_dir, "ds4.0")
os.makedirs(output_dir, exist_ok=True)

# 다운샘플링 
voxel_size = 4.0
start_frame = 1450
end_frame = 1749

for frame in range(start_frame, end_frame + 1):
    filename = f"redandblack_vox10_{frame:04d}.ply"
    file_path = os.path.join(base_dir, filename)

    if not os.path.exists(file_path):
        print(f"파일 없음: {file_path}")
        continue

    # 포인트 클라우드 데이터 파싱
    pcd = o3d.io.read_point_cloud(file_path)
    downsampled_pcd = pcd.voxel_down_sample(voxel_size=voxel_size)

    points = np.asarray(downsampled_pcd.points)
    colors = (np.asarray(downsampled_pcd.colors) * 255).astype(np.uint8)

    if points.size == 0:
        print(f"포인트 없음: {filename}")
        continue

    # 헤더 작성
    binary_header = f"""ply
format binary_little_endian 1.0
element vertex {len(points)}
property float x
property float y
property float z
property uchar red
property uchar green
property uchar blue
end_header
"""

    # 파일 저장 경로
    output_path = os.path.join(output_dir, f"redandblack_ds_{frame:04d}.ply")

    # 바이너리 파일로 저장
    with open(output_path, 'wb') as f:
        f.write(binary_header.encode('utf-8'))
        for p, c in zip(points, colors):
            f.write(struct.pack('<fffBBB', p[0], p[1], p[2], c[0], c[1], c[2]))

    print(f"저장 완료: {output_path}")
