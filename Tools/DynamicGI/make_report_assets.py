"""Builds report assets from an evaluation run (Captures/DynamicGI/eval -> Docs/DynamicGI).

Usage: python Tools/DynamicGI/make_report_assets.py
Creates labelled comparison sheets (GI on / GI off / indirect only) and a convergence summary
computed from series.csv (receiver irradiance per frame after each change).
"""
import csv
import json
import os
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
SRC = os.path.join(ROOT, 'Captures', 'DynamicGI', 'eval')
DST = os.path.join(ROOT, 'Docs', 'DynamicGI')
TILE_W, TILE_H = 640, 360


def font(size):
    for name in ('arial.ttf', 'segoeui.ttf', 'DejaVuSans.ttf'):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def tile(name, label):
    path = os.path.join(SRC, name + '.png')
    if not os.path.exists(path):
        img = Image.new('RGB', (TILE_W, TILE_H), (40, 0, 0))
    else:
        img = Image.open(path).convert('RGB').resize((TILE_W, TILE_H), Image.LANCZOS)
    d = ImageDraw.Draw(img)
    d.rectangle([0, 0, TILE_W, 26], fill=(0, 0, 0))
    d.text((6, 4), label, fill=(255, 255, 255), font=font(16))
    return img


def sheet(rows, out_name, title):
    cols = max(len(r) for r in rows)
    img = Image.new('RGB', (cols * TILE_W, len(rows) * TILE_H + 34), (20, 20, 20))
    ImageDraw.Draw(img).text((8, 6), title, fill=(255, 255, 255), font=font(20))
    for y, row in enumerate(rows):
        for x, (name, label) in enumerate(row):
            img.paste(tile(name, label), (x * TILE_W, 34 + y * TILE_H))
    img.save(os.path.join(DST, out_name), quality=88)
    print('wrote', out_name)


def convergence():
    path = os.path.join(SRC, 'series.csv')
    if not os.path.exists(path):
        return {}
    rows = list(csv.reader(open(path, encoding='utf-8')))
    header, data = rows[0], rows[1:]
    receivers = header[3:]
    records = {}
    for r in data:
        if len(r) < 4:
            continue
        records.setdefault(r[0], []).append((int(r[1]), float(r[2]), [float(x) for x in r[3:]]))
    summary = {}
    for name, series in records.items():
        out = {}
        for i, rec in enumerate(receivers):
            vals = [s[2][i] for s in series]
            if len(vals) < 10:
                continue
            final = sum(vals[-10:]) / 10.0           # mean of the last 10 frames
            start = vals[0]
            delta = final - start
            tol = max(abs(delta) * 0.1, abs(final) * 0.05, 3e-4)
            settled_frame, settled_t = None, None
            # smoothed (3-frame) value must stay inside the tolerance band until the end of the window
            sm = [sum(vals[max(0, k - 1):k + 2]) / len(vals[max(0, k - 1):k + 2]) for k in range(len(vals))]
            for k in range(len(sm) - 1, -1, -1):
                if abs(sm[k] - final) > tol:
                    break
                settled_frame, settled_t = series[k][0], series[k][1]
            out[rec] = {'start': round(start, 5), 'final': round(final, 5), 'settleFrame': settled_frame,
                        'settleSeconds': None if settled_t is None else round(settled_t, 3)}
        summary[name] = out
    json.dump(summary, open(os.path.join(DST, 'convergence.json'), 'w'), indent=1)
    print('wrote convergence.json')
    return summary


def main():
    os.makedirs(DST, exist_ok=True)
    steps = ['1 Intact', '2 Small wall hole', '3 Large wall hole', '4 Inner wall open', '5 Small ceiling hole', '6 Large ceiling holes', '7 Restored']
    rows = []
    for i, s in enumerate(steps, 1):
        rows.append([(f'A_step{i}_first_gi', f'Step {s} | GI on | first frame'),
                     (f'A_step{i}_gi', f'Step {s} | GI on | +4 s'),
                     (f'A_step{i}_off', f'Step {s} | GI off | +4 s'),
                     (f'A_step{i}_indirect', f'Step {s} | indirect only | +4 s')])
    sheet(rows[:4], 'steps_1-4_sunsky_lampsoff.jpg', 'Room A fixed view, Sun+Sky, interior lamps OFF, 1920x1080 captures (scaled)')
    sheet(rows[4:], 'steps_5-7_sunsky_lampsoff.jpg', 'Room A fixed view, Sun+Sky, interior lamps OFF, 1920x1080 captures (scaled)')
    rows = []
    for i in (1, 4, 7):
        rows.append([(f'A2_lamps_step{i}_gi', f'Step {i} | lamps ON | GI on'), (f'A2_lamps_step{i}_off', f'Step {i} | lamps ON | GI off'),
                     (f'A2_lamps_step{i}_indirect', f'Step {i} | lamps ON | indirect only')])
    sheet(rows, 'steps_lamps_on.jpg', 'Room A fixed view, Sun+Sky, interior lamps ON')
    rows = []
    for st in (2, 3, 4):
        rows.append([(f'B_step{st}_blocked_first_gi', f'Step {st} | B covered | first frame'), (f'B_step{st}_blocked_gi', f'Step {st} | B covered | +4 s'),
                     (f'B_step{st}_unblocked_first_gi', f'Step {st} | B removed | first frame'), (f'B_step{st}_unblocked_gi', f'Step {st} | B removed | +4 s')])
    sheet(rows, 'blocker_steps_2-4.jpg', 'Outside blocker (B), roof intact, Sun+Sky, lamps OFF, GI on')
    rows = []
    for mode in ('SunOnly', 'SkyOnly', 'SunAndSky'):
        rows.append([(f'C_step4_{mode}_lampsOff_gi', f'{mode} | lamps OFF | GI on'), (f'C_step4_{mode}_lampsOff_off', f'{mode} | lamps OFF | GI off'),
                     (f'C_step4_{mode}_lampsOn_gi', f'{mode} | lamps ON | GI on'), (f'C_step4_{mode}_lampsOn_indirect', f'{mode} | lamps ON | indirect only')])
    sheet(rows, 'lighting_modes_step4.jpg', 'Step 4, lighting input E x interior lamps I')
    rows = [[(f'D_step6_view{v}_gi', f'Step 6 | view {v} | GI on'), (f'D_step6_view{v}_off', f'Step 6 | view {v} | GI off'),
             (f'D_step6_view{v}_indirect', f'Step 6 | view {v} | indirect only')] for v in range(6)]
    sheet(rows, 'views_step6.jpg', 'Step 6, all fixture camera views (V), Sun+Sky, lamps OFF')
    sheet([[('G1_move_occluder_back_gi', 'G1 occluder at start | GI on'), ('G1_move_occluder_unpublished_first_gi', 'G1 moved (no event) | first frame'),
            ('G1_move_occluder_unpublished_gi', 'G1 moved (no event) | +4 s')],
           [('G2_add_red_panel_first_gi', 'G2 red panel added | first frame'), ('G2_add_red_panel_gi', 'G2 red panel added | +4 s'),
            ('G2_remove_red_panel_gi', 'G2 panel removed | +4 s')],
           [('G3_deform_mesh_in_place_first_gi', 'G3 mesh deformed | first frame'), ('G3_deform_mesh_in_place_gi', 'G3 mesh deformed | +4 s'),
            ('G3_restore_original_mesh_gi', 'G3 original mesh | +4 s')]],
          'generic_geometry.jpg', 'Generic edits outside the 7 steps (view: Indirect receiver B, step 4, Sun+Sky, lamps ON)')
    sheet([[('E_sealed_reference_gi', 'Step 1 (before damage) | GI on'), ('E_restored_after_step6_gi', 'Step 7 after step 6 | GI on')]],
          'restore_compare.jpg', 'Restore check: identical input, before damage vs. restored after the large roof breach')
    convergence()


if __name__ == '__main__':
    sys.exit(main())
