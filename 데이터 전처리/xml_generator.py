import os
import numpy as np
import open3d as o3d
import xml.etree.ElementTree as ET

start_frame = 1450      # 시작 프레임
end_frame = 1749        # 종료 프레임 (포함)

# 입력 경로
high_root = r"D:\3D\ply\redandblack\Ply\high_tile128"
low_root = r"D:\3D\ply\redandblack\Ply\low_tile128"

# 출력 파일 (폴더 경로 뒤에 파일명을 붙였습니다)
output_xml = r"D:\3D\ply\redandblack\Ply\tile_metadata_128.xml"
# ==========================================

# XML 루트 생성
tileset = ET.Element("Tileset")

print(f"[시작] 프레임 범위: {start_frame} ~ {end_frame}")
print(f"[경로] High: {high_root}")
print(f"[경로] Low: {low_root}")

for frame_idx in range(start_frame, end_frame + 1):
    frame_str = f"{frame_idx:04d}"

    high_frame_dir = os.path.join(high_root, f"high_{frame_str}")
    low_frame_dir = os.path.join(low_root, f"low_{frame_str}")

    # 디렉토리가 존재하는지 먼저 확인
    if not os.path.exists(high_frame_dir) or not os.path.exists(low_frame_dir):
        print(f"폴더 없음: {frame_str}")
        continue

    high_files = sorted([f for f in os.listdir(high_frame_dir) if f.endswith('.ply')])
    low_files = sorted([f for f in os.listdir(low_frame_dir) if f.endswith('.ply')])

    # Frame 블록 생성
    frame_elem = ET.SubElement(tileset, "Frame", {"id": frame_str})

    tile_id = 1  

    # 파일 개수가 다를 경우 안전하게 처리하기 위해 zip 사용 (더 적은 쪽 기준)
    for high_file, low_file in zip(high_files, low_files):
        high_path = os.path.join(high_frame_dir, high_file)
        low_path = os.path.join(low_frame_dir, low_file)

        # high 타일 읽기 (Open3D)
        try:
            pcd = o3d.io.read_point_cloud(high_path)
            points = np.asarray(pcd.points)
        except Exception as e:
            print(f"파일 읽기 실패: {high_path} / {e}")
            continue

        if points.size == 0:
            print(f"{high_path} -> 포인트 없음")
            continue

        center = points.mean(axis=0)

        # Tile 블록 생성
        tile_elem = ET.SubElement(frame_elem, "Tile", {
            "id": str(tile_id),
            "x": str(center[0]),
            "y": str(center[1]),
            "z": str(center[2])
        })

        ET.SubElement(tile_elem, "High", {
            "file": f"high_tile128/high_{frame_str}/{high_file}"
        })
        ET.SubElement(tile_elem, "Low", {
            "file": f"low_tile128/low_{frame_str}/{low_file}"
        })

        tile_id += 1

    print(f"[✅ 완료] frame {frame_str} -> {tile_id - 1}개 타일 기록됨")

# XML 저장
tree = ET.ElementTree(tileset)
ET.indent(tree, space="  ", level=0)
tree.write(output_xml, encoding="utf-8", xml_declaration=True)

print(f"\n{output_xml} 저장됨")