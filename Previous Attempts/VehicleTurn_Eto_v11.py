#! python3
# -*- coding: utf-8 -*-
"""
VehicleTurn_Eto_v11.py
Rhino 8 Python / RhinoCommon + Eto

Rigid-vehicle swept-path concept tool for architectural/site planning.

Key principles
- Select two straight 3D road centrelines: incoming, then outgoing.
- Lines may stop short, overlap, or cross: their virtual XY extensions are used.
- Inputs are NEVER modified.
- All committed output is ordinary Rhino curve geometry on the CURRENT layer.
- UI values are displayed in metres regardless of Rhino document units.
- Sloped 3D lines are supported; longitudinal grade is smoothly blended through the turn.
- No custom Rhino objects or plug-in-only geometry is created.

Important
This is a geometric design/prototyping tool, not a certified replacement for AutoTURN
or official Vejregler arealbehovskurver. V0.11 corrects the template reference-path model and makes swept-envelope generation robust:
- Vej template: the FRONT steering axle follows an exact tangent + circular arc + tangent path.
  The vehicle body/rear axle are solved kinematically from that prescribed front-axle path.
- Experimental SmartPath: the older continuously-steered rear-axle model remains available for comparison.
Standard AutoTURN turning templates define the centerline radius at the middle of the FRONT
steering axle group. Earlier versions incorrectly made the rear axle itself follow the tangent/arc
path. For REN at 35 degrees this is the difference between about 7.98 m front-axle radius and
6.54 m rear-axle steady-state radius.

V0.11 also unions footprint rectangles in batches. Rhino can fail when Curve.CreateBooleanUnion
is asked to union hundreds of highly-overlapping rectangles in one call; previous versions could
therefore silently show no swept envelope.
"""

import math
import Rhino
import Rhino.Geometry as rg
import Rhino.DocObjects as rd
import Rhino.Input as ri
import Rhino.Input.Custom as ric
import Rhino.Display as rdisp
import Rhino.UI as rui
import scriptcontext as sc
import System.Drawing as sd

import Eto.Forms as ef
import Eto.Drawing as ed
from Rhino.UI import RhinoEtoApp, EtoExtensions


# -----------------------------------------------------------------------------
# Vehicle presets, metres
# -----------------------------------------------------------------------------
# width, front overhang, wheelbase, rear overhang, maximum steer in Mode B
VEHICLES = {
    "PV": {
        "label": "PV - Passenger car / van",
        "width_m": 1.85,
        "front_overhang_m": 0.95,
        "wheelbase_m": 2.75,
        "rear_overhang_m": 1.10,
        "steer_b_deg": 36.0,
    },
    "REN": {
        "label": "REN - Refuse / 10 m rigid truck",
        "width_m": 2.55,
        "front_overhang_m": 1.85,
        "wheelbase_m": 4.58,
        "rear_overhang_m": 3.19,
        "steer_b_deg": 35.0,
    },
    "LV12": {
        "label": "LV12 - 12 m rigid truck",
        "width_m": 2.55,
        "front_overhang_m": 1.45,
        "wheelbase_m": 6.80,
        "rear_overhang_m": 3.75,
        "steer_b_deg": 39.0,
    },
    "BUS12": {
        "label": "BUS12 - 12 m bus",
        "width_m": 2.55,
        "front_overhang_m": 2.72,
        "wheelbase_m": 6.00,
        "rear_overhang_m": 3.28,
        "steer_b_deg": 45.0,
    },
}
VEHICLE_KEYS = ["PV", "REN", "LV12", "BUS12"]
# Vejregler: mode A maximum wheel angle is 33 gon = 29.7 degrees.
MODE_A_STEER_DEG = 33.0 * 0.9
MODE_SPEED_KMH = {"A": 15.0, "B": 5.0}
# AutoTURN standard design vehicles commonly use a conservative 6 s lock-to-lock time
# at low speeds.  V0.8 exposes this value rather than inventing a transition length.
DEFAULT_LOCK_TO_LOCK_S = 6.0


# -----------------------------------------------------------------------------
# Helpers
# -----------------------------------------------------------------------------
def clamp(x, a, b):
    return max(a, min(b, x))


def cross2(ax, ay, bx, by):
    return ax * by - ay * bx


def dot2(a, b):
    return a[0] * b[0] + a[1] * b[1]


def unit2(x, y):
    L = math.hypot(x, y)
    if L <= 1e-12:
        return None
    return (x / L, y / L)


def left2(d):
    return (-d[1], d[0])


def p2_add(p, d, s=1.0):
    return (p[0] + d[0] * s, p[1] + d[1] * s)


_UNIT_CACHE_SYSTEM = None
_UNIT_TO_MODEL = 1.0
_UNIT_FROM_MODEL = 1.0
STICKY_PREFIX = "VehicleTurn11."


def _refresh_unit_cache():
    global _UNIT_CACHE_SYSTEM, _UNIT_TO_MODEL, _UNIT_FROM_MODEL
    us = sc.doc.ModelUnitSystem
    if _UNIT_CACHE_SYSTEM != us:
        _UNIT_CACHE_SYSTEM = us
        _UNIT_TO_MODEL = Rhino.RhinoMath.UnitScale(Rhino.UnitSystem.Meters, us)
        _UNIT_FROM_MODEL = Rhino.RhinoMath.UnitScale(us, Rhino.UnitSystem.Meters)


def metres_to_model(v):
    _refresh_unit_cache()
    return float(v) * _UNIT_TO_MODEL


def model_to_metres(v):
    _refresh_unit_cache()
    return float(v) * _UNIT_FROM_MODEL


def sticky_get(key, default):
    full = STICKY_PREFIX + key
    return sc.sticky[full] if full in sc.sticky else default


def sticky_int(key, default, lo=None, hi=None):
    try:
        v = int(sticky_get(key, default))
    except Exception:
        v = int(default)
    if lo is not None:
        v = max(int(lo), v)
    if hi is not None:
        v = min(int(hi), v)
    return v


def sticky_float(key, default, lo=None, hi=None):
    try:
        v = float(sticky_get(key, default))
        if not math.isfinite(v):
            raise ValueError()
    except Exception:
        v = float(default)
    if lo is not None:
        v = max(float(lo), v)
    if hi is not None:
        v = min(float(hi), v)
    return v


def sticky_bool(key, default):
    v = sticky_get(key, default)
    if isinstance(v, bool):
        return v
    if isinstance(v, (int, float)):
        return bool(v)
    if isinstance(v, str):
        return v.strip().lower() in ("1", "true", "yes", "on")
    return bool(default)


# -----------------------------------------------------------------------------
# Input geometry
# -----------------------------------------------------------------------------
def get_linear_curve(prompt):
    go = ric.GetObject()
    go.SetCommandPrompt(prompt)
    go.GeometryFilter = rd.ObjectType.Curve
    go.SubObjectSelect = False
    try:
        go.EnablePreSelect(False, True)
    except Exception:
        pass
    go.Get()
    if go.CommandResult() != Rhino.Commands.Result.Success:
        return None, None, None

    objref = go.Object(0)
    crv = objref.Curve()
    tol = sc.doc.ModelAbsoluteTolerance
    if crv is None or crv.IsClosed or not crv.IsLinear(tol):
        rui.Dialogs.ShowMessage("Selected centreline must be an OPEN straight line or linear curve.", "VehicleTurn")
        try:
            sc.doc.Objects.UnselectAll()
        except Exception:
            pass
        return None, None, None

    line = rg.Line(crv.PointAtStart, crv.PointAtEnd)
    pick = objref.SelectionPoint()
    if not pick.IsValid:
        pick = crv.PointAt(crv.Domain.Mid)
    try:
        sc.doc.Objects.UnselectAll()
        sc.doc.Views.Redraw()
    except Exception:
        pass
    return objref, line, pick

def infinite_plan_intersection(line_a, line_b):
    p = (line_a.From.X, line_a.From.Y)
    r = (line_a.To.X - line_a.From.X, line_a.To.Y - line_a.From.Y)
    q = (line_b.From.X, line_b.From.Y)
    s = (line_b.To.X - line_b.From.X, line_b.To.Y - line_b.From.Y)

    den = cross2(r[0], r[1], s[0], s[1])
    scale = max(math.hypot(*r), math.hypot(*s), 1.0)
    if abs(den) <= 1e-10 * scale * scale:
        return None

    qp = (q[0] - p[0], q[1] - p[1])
    t = cross2(qp[0], qp[1], s[0], s[1]) / den
    return (p[0] + t * r[0], p[1] + t * r[1])


def line_z_at_xy(line, xy):
    rx = line.To.X - line.From.X
    ry = line.To.Y - line.From.Y
    rr = rx * rx + ry * ry
    if rr <= 1e-20:
        return line.From.Z
    t = ((xy[0] - line.From.X) * rx + (xy[1] - line.From.Y) * ry) / rr
    return line.From.Z + t * (line.To.Z - line.From.Z)


def line_grade_along_xy_direction(line, travel_dir_xy):
    rx = line.To.X - line.From.X
    ry = line.To.Y - line.From.Y
    h = math.hypot(rx, ry)
    if h <= 1e-12:
        return 0.0
    raw_dir = (rx / h, ry / h)
    raw_grade = (line.To.Z - line.From.Z) / h
    return raw_grade if dot2(raw_dir, travel_dir_xy) >= 0.0 else -raw_grade


def travel_directions(line_in, pick_in, line_out, pick_out):
    """Return the virtual road-centre intersection and intended travel directions."""
    ip = infinite_plan_intersection(line_in, line_out)
    if ip is None:
        return None, None, None, "Centrelines are parallel in plan."
    pin = (pick_in.X, pick_in.Y)
    pout = (pick_out.X, pick_out.Y)
    d_in = unit2(ip[0] - pin[0], ip[1] - pin[1])
    d_out = unit2(pout[0] - ip[0], pout[1] - ip[1])
    if d_in is None or d_out is None:
        return None, None, None, "Pick each centreline away from the virtual intersection."
    return ip, d_in, d_out, None


def shifted_line_xy(line, travel_dir, lateral):
    """Shift a 3D straight line laterally in plan, preserving its longitudinal grade.

    Positive lateral is left of travel; negative is right of travel.
    """
    n = left2(travel_dir)
    dx = n[0] * lateral
    dy = n[1] * lateral
    return rg.Line(
        rg.Point3d(line.From.X + dx, line.From.Y + dy, line.From.Z),
        rg.Point3d(line.To.X + dx, line.To.Y + dy, line.To.Z),
    )


def shifted_pick_xy(pick, travel_dir, lateral):
    n = left2(travel_dir)
    return rg.Point3d(pick.X + n[0] * lateral, pick.Y + n[1] * lateral, pick.Z)


def make_vehicle_path_inputs(line_in, pick_in, line_out, pick_out, road_width, road_layout):
    """Create the lines followed by the rear axle.

    For a two-way road in right-hand traffic, the nominal vehicle path is placed
    at the centre of the right-hand lane (road width / 4 from the road centreline).
    For a single-lane/shared road, the nominal vehicle path stays on the centreline.
    """
    ip, d_in, d_out, err = travel_directions(line_in, pick_in, line_out, pick_out)
    if err:
        return None, err

    lane_offset = 0.0
    if road_layout == "two_way" and road_width > 0.0:
        lane_offset = -road_width * 0.25  # right side of travel, equal two-lane assumption

    path_in = shifted_line_xy(line_in, d_in, lane_offset)
    path_out = shifted_line_xy(line_out, d_out, lane_offset)
    path_pick_in = shifted_pick_xy(pick_in, d_in, lane_offset)
    path_pick_out = shifted_pick_xy(pick_out, d_out, lane_offset)

    return {
        "road_intersection": ip,
        "road_d_in": d_in,
        "road_d_out": d_out,
        "lane_offset": lane_offset,
        "path_line_in": path_in,
        "path_line_out": path_out,
        "path_pick_in": path_pick_in,
        "path_pick_out": path_pick_out,
    }, None


# -----------------------------------------------------------------------------
# Turn solver
# -----------------------------------------------------------------------------
def default_lock_to_lock_s():
    return DEFAULT_LOCK_TO_LOCK_S

def build_local_steered_path(angle, turn_sign, radius, lock_to_lock_s, speed, wheelbase, steering_lock_deg, step, steering_model="continuous"):
    """Build a local rear-axle path.

    steering_model == "minimum":
        Wheels are set to the requested steering angle before movement starts. The rear axle
        follows an exact circular arc. This is a geometric minimum-space / constant-lock model.

    steering_model == "continuous":
        Steering angle changes linearly in time based on speed and lock-to-lock time.
        The numerical profile is solved until its integrated final heading matches the target,
        rather than patching the last sample's heading afterward.

    The single-track/bicycle model is:
        curvature = tan(delta) / wheelbase
    """
    angle = abs(float(angle))
    radius = max(float(radius), 1e-9)
    step = max(float(step), 1e-6)
    speed = max(float(speed), 1e-9)
    wheelbase = max(float(wheelbase), 1e-9)
    lock_to_lock_s = max(float(lock_to_lock_s), 0.05)

    delta_lock = math.radians(max(float(steering_lock_deg), 0.1))
    delta_requested = math.atan(wheelbase / radius)
    delta_target = min(delta_requested, delta_lock)

    if steering_model == "minimum":
        peak_k_abs = math.tan(delta_target) / wheelbase if delta_target > 1e-12 else 0.0
        if peak_k_abs <= 1e-12:
            return None
        R = 1.0 / peak_k_abs
        arc_len = R * angle
        n = max(2, int(math.ceil(arc_len / step)))
        samples = []
        for i in range(n + 1):
            u = float(i) / float(n)
            a = angle * u
            h = turn_sign * a
            x = R * math.sin(a)
            y = turn_sign * R * (1.0 - math.cos(a))
            samples.append({
                "s": arc_len * u, "x": x, "y": y, "heading": h,
                "curvature": turn_sign * peak_k_abs,
                "steer": turn_sign * delta_target, "phase": "arc",
            })

        end_x = samples[-1]["x"]
        end_y = samples[-1]["y"]
        sh = math.sin(turn_sign * angle)
        if abs(sh) <= 1e-10:
            return None
        back = end_y / sh
        tangent_distance = end_x - back * math.cos(turn_sign * angle)
        if tangent_distance <= 0.0:
            return None

        return {
            "samples": samples,
            "ramp_len": 0.0,
            "ramp_time": 0.0,
            "const_len": arc_len,
            "const_time": arc_len / speed,
            "k_peak": peak_k_abs,
            "steer_peak_deg": math.degrees(delta_target),
            "steer_target_deg": math.degrees(delta_target),
            "steer_rate_deg_s": 0.0,
            "actual_radius": R,
            "length": arc_len,
            "tangent_distance": tangent_distance,
            "steering_model": "minimum",
        }

    steer_rate = (2.0 * delta_lock) / lock_to_lock_s  # rad/s, max-left to max-right
    max_dt = step / speed

    def numerical_integral(delta_peak, t_const, record=False):
        """Integrate symmetric steer-in / constant / steer-out profile."""
        t_ramp = delta_peak / steer_rate if steer_rate > 1e-12 else 0.0
        x = y = h = station = 0.0
        samples = [{
            "s": 0.0, "x": 0.0, "y": 0.0, "heading": 0.0,
            "curvature": 0.0, "steer": 0.0, "phase": "steer_in",
        }] if record else None

        def segment(name, duration):
            nonlocal x, y, h, station
            if duration <= 1e-12:
                return
            n = max(1, int(math.ceil(duration / max_dt)))
            dt = duration / float(n)
            ds = speed * dt
            for i in range(n):
                u_mid = (i + 0.5) / float(n)
                if name == "steer_in":
                    delta = delta_peak * u_mid
                elif name == "arc":
                    delta = delta_peak
                else:
                    delta = delta_peak * (1.0 - u_mid)
                k = turn_sign * math.tan(delta) / wheelbase
                dh = k * ds
                hm = h + 0.5 * dh
                x += math.cos(hm) * ds
                y += math.sin(hm) * ds
                h += dh
                station += ds
                if record:
                    samples.append({
                        "s": station, "x": x, "y": y, "heading": h,
                        "curvature": k, "steer": turn_sign * delta, "phase": name,
                    })

        segment("steer_in", t_ramp)
        segment("arc", t_const)
        segment("steer_out", t_ramp)
        return x, y, h, station, samples, t_ramp

    # Analytic heading during one linear steering ramp is useful for choosing the
    # trapezoid/triangle branch, but the final parameters are refined using the same
    # numerical integrator used to generate positions.
    def ramp_heading(delta):
        c = max(math.cos(delta), 1e-12)
        return (speed / (wheelbase * steer_rate)) * (-math.log(c))

    full_two_ramps = 2.0 * ramp_heading(delta_target)
    target_abs = angle

    if target_abs >= full_two_ramps - 1e-12:
        delta_peak = delta_target
        yaw_rate_const = (speed / wheelbase) * math.tan(delta_peak)
        t_guess = max(0.0, (target_abs - full_two_ramps) / max(yaw_rate_const, 1e-12))

        def heading_abs_for_t(t):
            return abs(numerical_integral(delta_peak, max(0.0, t), False)[2])

        lo = 0.0
        hi = max(t_guess * 1.5 + 0.1, 0.1)
        while heading_abs_for_t(hi) < target_abs and hi < 1e4:
            hi *= 2.0
        for _ in range(48):
            mid = 0.5 * (lo + hi)
            if heading_abs_for_t(mid) < target_abs:
                lo = mid
            else:
                hi = mid
        t_const = 0.5 * (lo + hi)
    else:
        t_const = 0.0

        def heading_abs_for_delta(delta):
            return abs(numerical_integral(delta, 0.0, False)[2])

        lo = 0.0
        hi = delta_target
        for _ in range(52):
            mid = 0.5 * (lo + hi)
            if heading_abs_for_delta(mid) < target_abs:
                lo = mid
            else:
                hi = mid
        delta_peak = 0.5 * (lo + hi)

    x, y, h, station, samples, t_ramp = numerical_integral(delta_peak, t_const, True)
    target_h = turn_sign * angle
    heading_error = h - target_h
    if abs(heading_error) > 5e-6:
        # Should be well below this after the scalar solve. Do not silently rotate the
        # final pose; rejecting it is safer than mixing a patched heading with old XY.
        return None

    sh = math.sin(target_h)
    if abs(sh) <= 1e-10:
        return None
    back = y / sh
    tangent_distance = x - back * math.cos(target_h)
    if tangent_distance <= 0.0:
        return None

    peak_k = math.tan(delta_peak) / wheelbase if delta_peak > 1e-12 else 0.0
    return {
        "samples": samples,
        "ramp_len": speed * t_ramp,
        "ramp_time": t_ramp,
        "const_len": speed * t_const,
        "const_time": t_const,
        "k_peak": peak_k,
        "steer_peak_deg": math.degrees(delta_peak),
        "steer_target_deg": math.degrees(delta_target),
        "steer_rate_deg_s": math.degrees(steer_rate),
        "actual_radius": 1.0 / peak_k if peak_k > 1e-12 else 1e99,
        "length": station,
        "tangent_distance": tangent_distance,
        "heading_error": heading_error,
        "steering_model": "continuous",
    }

def solve_turn(line_in, pick_in, line_out, pick_out, radius, lock_to_lock_s, vehicle_key, mode, steering_model="continuous"):
    """Solve a smooth symmetric rear-axle path using infinite XY line extensions."""
    ip = infinite_plan_intersection(line_in, line_out)
    if ip is None:
        return None, "Centrelines are parallel in plan."

    pin = (pick_in.X, pick_in.Y)
    pout = (pick_out.X, pick_out.Y)

    # Pick position defines travel side/direction, not the curve's own direction.
    d_in = unit2(ip[0] - pin[0], ip[1] - pin[1])
    d_out = unit2(pout[0] - ip[0], pout[1] - ip[1])
    if d_in is None or d_out is None:
        return None, "Pick each centreline away from the virtual intersection."

    c = clamp(dot2(d_in, d_out), -1.0, 1.0)
    angle = math.acos(c)
    sign_val = cross2(d_in[0], d_in[1], d_out[0], d_out[1])

    if abs(sign_val) < 1e-8 or angle < math.radians(1.0):
        return None, "The selected travel directions are effectively straight/collinear."
    if angle > math.radians(175.0):
        return None, "Near-U-turn geometry is not supported in this version."

    turn_sign = 1.0 if sign_val > 0.0 else -1.0
    dims = vehicle_dims_model(vehicle_key)
    speed = metres_to_model(MODE_SPEED_KMH[mode] / 3.6)
    profile = build_local_steered_path(
        angle,
        turn_sign,
        radius,
        lock_to_lock_s,
        speed,
        dims["wheelbase"],
        max_steer_deg(vehicle_key, mode),
        max(sc.doc.ModelAbsoluteTolerance * 5.0, metres_to_model(0.05)),
        steering_model,
    )
    if profile is None:
        return None, "Could not construct steering-transition path."

    tangent = profile["tangent_distance"]
    local_side = left2(d_in)

    def local_to_xy(x, y):
        # Local start tangent intersection is at x=tangent; map it to the virtual road intersection.
        return (
            ip[0] + d_in[0] * (x - tangent) + local_side[0] * y,
            ip[1] + d_in[1] * (x - tangent) + local_side[1] * y,
        )

    start_xy = local_to_xy(0.0, 0.0)
    last = profile["samples"][-1]
    end_xy = local_to_xy(last["x"], last["y"])

    z_start = line_z_at_xy(line_in, start_xy)
    z_end = line_z_at_xy(line_out, end_xy)
    g1 = line_grade_along_xy_direction(line_in, d_in)
    g2 = line_grade_along_xy_direction(line_out, d_out)

    return {
        "intersection": ip,
        "d_in": d_in,
        "d_out": d_out,
        "local_side": local_side,
        "turn_sign": turn_sign,
        "angle": angle,
        "radius": radius,
        "lock_to_lock_s": lock_to_lock_s,
        "start_xy": start_xy,
        "end_xy": end_xy,
        "t1": start_xy,
        "t2": end_xy,
        "z_t1": z_start,
        "z_t2": z_end,
        "g1": g1,
        "g2": g2,
        "profile": profile,
        "local_to_xy": local_to_xy,
    }, None



def solve_front_template(line_in, pick_in, line_out, pick_out, front_radius, vehicle_key, mode):
    """Solve an AutoTURN-standard-template style FRONT-axle tangent/arc/tangent path.

    The selected (or lane-shifted) centrelines are the front steering axle's approach and
    departure tangents. The body/rear axle do NOT follow that arc directly; sample_front_template
    solves their lag/offtracking kinematically.
    """
    ip = infinite_plan_intersection(line_in, line_out)
    if ip is None:
        return None, "Centrelines are parallel in plan."

    pin = (pick_in.X, pick_in.Y)
    pout = (pick_out.X, pick_out.Y)
    d_in = unit2(ip[0] - pin[0], ip[1] - pin[1])
    d_out = unit2(pout[0] - ip[0], pout[1] - ip[1])
    if d_in is None or d_out is None:
        return None, "Pick each centreline away from the virtual intersection."

    c = clamp(dot2(d_in, d_out), -1.0, 1.0)
    angle = math.acos(c)
    sign_val = cross2(d_in[0], d_in[1], d_out[0], d_out[1])
    if abs(sign_val) < 1e-8 or angle < math.radians(1.0):
        return None, "The selected travel directions are effectively straight/collinear."
    if angle > math.radians(175.0):
        return None, "Near-U-turn geometry is not supported in this version."
    turn_sign = 1.0 if sign_val > 0.0 else -1.0

    min_rf = min_front_radius_model(vehicle_key, mode)
    rf = max(float(front_radius), min_rf)
    tangent = rf * math.tan(angle * 0.5)
    t1 = p2_add(ip, d_in, -tangent)
    t2 = p2_add(ip, d_out, tangent)
    n1 = left2(d_in)
    center = (t1[0] + n1[0] * turn_sign * rf,
              t1[1] + n1[1] * turn_sign * rf)

    z1 = line_z_at_xy(line_in, t1)
    z2 = line_z_at_xy(line_out, t2)
    g1 = line_grade_along_xy_direction(line_in, d_in)
    g2 = line_grade_along_xy_direction(line_out, d_out)

    return {
        "intersection": ip,
        "d_in": d_in,
        "d_out": d_out,
        "turn_sign": turn_sign,
        "angle": angle,
        "front_radius": rf,
        "radius": rf,
        "center": center,
        "t1": t1,
        "t2": t2,
        "start_xy": t1,
        "end_xy": t2,
        "z_t1": z1,
        "z_t2": z2,
        "g1": g1,
        "g2": g2,
        "profile": {
            "tangent_distance": tangent,
            "actual_radius": rf,
            "front_radius": rf,
            "rear_steady_radius": math.sqrt(max(rf * rf - vehicle_dims_model(vehicle_key)["wheelbase"] ** 2, 0.0)),
            "length": rf * angle,
            "steer_peak_deg": 0.0,
            "steer_target_deg": max_steer_deg(vehicle_key, mode),
            "ramp_len": 0.0,
            "ramp_time": 0.0,
            "steering_model": "template",
        },
        "template_model": True,
    }, None


def _wrap_angle(a):
    while a > math.pi:
        a -= 2.0 * math.pi
    while a < -math.pi:
        a += 2.0 * math.pi
    return a


def _rk4_heading_step(theta, s0, ds, psi_func, wheelbase):
    """Integrate d(theta)/ds_front = sin(psi_front-theta)/wheelbase."""
    def f(ss, th):
        return math.sin(_wrap_angle(psi_func(ss) - th)) / wheelbase
    k1 = f(s0, theta)
    k2 = f(s0 + 0.5 * ds, theta + 0.5 * ds * k1)
    k3 = f(s0 + 0.5 * ds, theta + 0.5 * ds * k2)
    k4 = f(s0 + ds, theta + ds * k3)
    return theta + (ds / 6.0) * (k1 + 2.0*k2 + 2.0*k3 + k4)


def sample_front_template(turn, lead, step, vehicle_key):
    """Sample rear-axle/body poses while the FRONT axle follows line + arc + line.

    This distinction is important: AutoTURN's standard-template 'Centerline' radius is measured
    at the front steering axle, not at the rear axle. The rear axle therefore offtracks naturally.
    """
    dims = vehicle_dims_model(vehicle_key)
    wb = dims["wheelbase"]
    d1 = turn["d_in"]
    d2 = turn["d_out"]
    t1 = turn["t1"]
    t2 = turn["t2"]
    ctr = turn["center"]
    rf = turn["front_radius"]
    sign = turn["turn_sign"]
    angle = turn["angle"]
    z1 = turn["z_t1"]
    z2 = turn["z_t2"]
    g1 = turn["g1"]
    g2 = turn["g2"]
    base_h = math.atan2(d1[1], d1[0])
    out_h = math.atan2(d2[1], d2[0])
    arc_len = rf * angle
    step = max(step, sc.doc.ModelAbsoluteTolerance * 10.0)

    poses = []
    prev_rear = None
    station = 0.0
    peak_steer = 0.0

    def add_pose(front_xy, front_z, theta, psi, grade, phase):
        nonlocal prev_rear, station, peak_steer
        steer = _wrap_angle(psi - theta)
        peak_steer = max(peak_steer, abs(steer))
        rear_xy = (front_xy[0] - wb * math.cos(theta),
                   front_xy[1] - wb * math.sin(theta))
        # Keep the rigid wheelbase approximately on the longitudinal grade in 3D.
        rear_z = front_z - wb * grade
        rp = rg.Point3d(rear_xy[0], rear_xy[1], rear_z)
        fp = rg.Point3d(front_xy[0], front_xy[1], front_z)
        if prev_rear is not None:
            station += math.hypot(rp.X - prev_rear.X, rp.Y - prev_rear.Y)
        poses.append({
            "p": rp, "front_p": fp, "heading": theta, "front_heading": psi,
            "grade": grade, "station": station, "phase": phase,
            "curvature": 0.0, "steer": steer,
        })
        prev_rear = rp

    # Incoming front-axle tangent. Vehicle is aligned with it.
    n_in = max(2, int(math.ceil(lead / step)) + 1)
    theta = base_h
    for i in range(n_in):
        u = float(i) / float(n_in - 1)
        srel = -lead + lead * u
        fxy = p2_add(t1, d1, srel)
        fz = z1 + g1 * srel
        add_pose(fxy, fz, theta, base_h, g1, "in")

    # Circular FRONT-axle path. Integrate body heading/rear-axle lag.
    n_arc = max(4, int(math.ceil(arc_len / step)))
    psi_func = lambda ss: base_h + sign * (ss / rf)
    s_prev = 0.0
    for i in range(1, n_arc + 1):
        s_now = arc_len * float(i) / float(n_arc)
        ds = s_now - s_prev
        theta = _rk4_heading_step(theta, s_prev, ds, psi_func, wb)
        a = s_now / rf
        # Exact front axle circle in local tangent coordinates.
        fxy = (
            t1[0] + d1[0] * (rf * math.sin(a)) + left2(d1)[0] * (sign * rf * (1.0 - math.cos(a))),
            t1[1] + d1[1] * (rf * math.sin(a)) + left2(d1)[1] * (sign * rf * (1.0 - math.cos(a))),
        )
        fz, grade = hermite_z(z1, z2, g1, g2, arc_len, s_now)
        add_pose(fxy, fz, theta, psi_func(s_now), grade, "turn")
        s_prev = s_now

    # Exit front-axle tangent. Steering/body heading settles naturally toward the tangent.
    n_out = max(2, int(math.ceil(lead / step)) + 1)
    psi_const = lambda ss: out_h
    s_prev = 0.0
    for i in range(1, n_out):
        s_now = lead * float(i) / float(n_out - 1)
        ds = s_now - s_prev
        theta = _rk4_heading_step(theta, s_prev, ds, psi_const, wb)
        fxy = p2_add(t2, d2, s_now)
        fz = z2 + g2 * s_now
        add_pose(fxy, fz, theta, out_h, g2, "out")
        s_prev = s_now

    turn["profile"]["steer_peak_deg"] = math.degrees(peak_steer)
    turn["profile"]["length"] = arc_len
    return poses


def exact_front_template_curve(turn, lead, poses=None):
    """Native Rhino front-axle path: line + exact arc + line when flat."""
    d1 = turn["d_in"]
    d2 = turn["d_out"]
    t1 = turn["t1"]
    t2 = turn["t2"]
    z1 = turn["z_t1"]
    z2 = turn["z_t2"]
    g1 = turn["g1"]
    g2 = turn["g2"]
    rf = turn["front_radius"]
    sign = turn["turn_sign"]
    angle = turn["angle"]

    p0 = rg.Point3d(t1[0] - d1[0]*lead, t1[1] - d1[1]*lead, z1 - g1*lead)
    p1 = rg.Point3d(t1[0], t1[1], z1)
    p2 = rg.Point3d(t2[0], t2[1], z2)
    p3 = rg.Point3d(t2[0] + d2[0]*lead, t2[1] + d2[1]*lead, z2 + g2*lead)
    if abs(g1) < 1e-9 and abs(g2) < 1e-9 and abs(z2-z1) < sc.doc.ModelAbsoluteTolerance*2.0:
        a = 0.5 * angle
        midxy = (
            t1[0] + d1[0]*(rf*math.sin(a)) + left2(d1)[0]*(sign*rf*(1.0-math.cos(a))),
            t1[1] + d1[1]*(rf*math.sin(a)) + left2(d1)[1]*(sign*rf*(1.0-math.cos(a))),
        )
        pm = rg.Point3d(midxy[0], midxy[1], z1)
        try:
            arc = rg.Arc(p1, pm, p2)
            return join_curve_segments([rg.LineCurve(p0,p1), rg.ArcCurve(arc), rg.LineCurve(p2,p3)])
        except Exception:
            pass
    if poses:
        return trajectory_curve(poses, lambda pose: pose.get("front_p", pose["p"]), tangent_getter=lambda pose: rg.Vector3d(math.cos(pose.get("front_heading", pose["heading"])), math.sin(pose.get("front_heading", pose["heading"])), pose.get("grade",0.0)))
    return join_curve_segments([rg.LineCurve(p0,p1), rg.LineCurve(p1,p2), rg.LineCurve(p2,p3)])

def hermite_z(z0, z1, g0, g1, L, s):
    """Cubic vertical blend that matches elevation and grade at both ends of the manoeuvre."""
    if L <= 1e-12:
        return z0, g0
    u = clamp(s / L, 0.0, 1.0)
    u2 = u * u
    u3 = u2 * u

    h00 = 2.0 * u3 - 3.0 * u2 + 1.0
    h10 = u3 - 2.0 * u2 + u
    h01 = -2.0 * u3 + 3.0 * u2
    h11 = u3 - u2
    z = h00 * z0 + h10 * L * g0 + h01 * z1 + h11 * L * g1

    dh00 = 6.0 * u2 - 6.0 * u
    dh10 = 3.0 * u2 - 4.0 * u + 1.0
    dh01 = -6.0 * u2 + 6.0 * u
    dh11 = 3.0 * u2 - 2.0 * u
    dz_du = dh00 * z0 + dh10 * L * g0 + dh01 * z1 + dh11 * L * g1
    return z, dz_du / L


def sample_turn(turn, lead, step):
    """Sample poses along run-in + manoeuvre + run-out."""
    poses = []
    d1 = turn["d_in"]
    d2 = turn["d_out"]
    start_xy = turn["start_xy"]
    end_xy = turn["end_xy"]
    z1 = turn["z_t1"]
    z2 = turn["z_t2"]
    g1 = turn["g1"]
    g2 = turn["g2"]
    Lturn = turn["profile"]["length"]
    base_heading = math.atan2(d1[1], d1[0])

    step = max(step, sc.doc.ModelAbsoluteTolerance * 10.0)
    station = 0.0
    prev_pt = None

    n_in = max(2, int(math.ceil(lead / step)) + 1)
    for i in range(n_in - 1):
        frac = float(i) / float(n_in - 1)
        s_local = -lead + lead * frac
        xy = p2_add(start_xy, d1, s_local)
        z = z1 + g1 * s_local
        pt = rg.Point3d(xy[0], xy[1], z)
        if prev_pt is not None:
            station += math.hypot(pt.X - prev_pt.X, pt.Y - prev_pt.Y)
        poses.append({
            "p": pt, "heading": base_heading, "grade": g1,
            "station": station, "phase": "in", "curvature": 0.0, "steer": 0.0,
        })
        prev_pt = pt

    for smp in turn["profile"]["samples"]:
        xy = turn["local_to_xy"](smp["x"], smp["y"])
        z, grade = hermite_z(z1, z2, g1, g2, Lturn, smp["s"])
        pt = rg.Point3d(xy[0], xy[1], z)
        if prev_pt is not None:
            station += math.hypot(pt.X - prev_pt.X, pt.Y - prev_pt.Y)
        poses.append({
            "p": pt,
            "heading": base_heading + smp["heading"],
            "grade": grade,
            "station": station,
            "phase": "turn",
            "curvature": smp.get("curvature", 0.0),
            "steer": smp.get("steer", 0.0),
        })
        prev_pt = pt

    n_out = max(2, int(math.ceil(lead / step)) + 1)
    for i in range(1, n_out):
        frac = float(i) / float(n_out - 1)
        s_local = lead * frac
        xy = p2_add(end_xy, d2, s_local)
        z = z2 + g2 * s_local
        pt = rg.Point3d(xy[0], xy[1], z)
        station += math.hypot(pt.X - prev_pt.X, pt.Y - prev_pt.Y)
        poses.append({
            "p": pt,
            "heading": math.atan2(d2[1], d2[0]),
            "grade": g2,
            "station": station,
            "phase": "out",
            "curvature": 0.0,
            "steer": 0.0,
        })
        prev_pt = pt

    return poses

# -----------------------------------------------------------------------------
# Vehicle + swept geometry
# -----------------------------------------------------------------------------
def vehicle_dims_model(vehicle_key):
    v = VEHICLES[vehicle_key]
    return {
        "width": metres_to_model(v["width_m"]),
        "front_overhang": metres_to_model(v["front_overhang_m"]),
        "wheelbase": metres_to_model(v["wheelbase_m"]),
        "rear_overhang": metres_to_model(v["rear_overhang_m"]),
        "length": metres_to_model(v["front_overhang_m"] + v["wheelbase_m"] + v["rear_overhang_m"]),
    }


def max_steer_deg(vehicle_key, mode):
    mechanical = float(VEHICLES[vehicle_key]["steer_b_deg"])
    return min(MODE_A_STEER_DEG, mechanical) if mode == "A" else mechanical

def min_rear_radius_model(vehicle_key, mode):
    dims = vehicle_dims_model(vehicle_key)
    steer = math.radians(max_steer_deg(vehicle_key, mode))
    return dims["wheelbase"] / math.tan(steer)


def min_rear_radius_m(vehicle_key, mode):
    return model_to_metres(min_rear_radius_model(vehicle_key, mode))


def min_front_radius_model(vehicle_key, mode):
    """Minimum FRONT steering-axle centerline radius.

    AutoTURN defines Centerline radius at the middle of the front steering axle group.
    For the bicycle geometry: sin(delta) = wheelbase / R_front.
    """
    dims = vehicle_dims_model(vehicle_key)
    steer = math.radians(max_steer_deg(vehicle_key, mode))
    return dims["wheelbase"] / max(math.sin(steer), 1e-12)


def min_front_radius_m(vehicle_key, mode):
    return model_to_metres(min_front_radius_model(vehicle_key, mode))


def pose_frame(pose):
    h = pose["heading"]
    g = pose["grade"]
    fwd = rg.Vector3d(math.cos(h), math.sin(h), g)
    if not fwd.Unitize():
        fwd = rg.Vector3d(math.cos(h), math.sin(h), 0.0)
    side = rg.Vector3d(-math.sin(h), math.cos(h), 0.0)
    side.Unitize()
    return fwd, side


def point_offset(base, fwd, side, along, lateral):
    return base + fwd * along + side * lateral


def clearance_corners(pose, dims, clearance):
    p = pose["p"]
    fwd, side = pose_frame(pose)
    half_w = dims["width"] * 0.5 + clearance
    front = dims["wheelbase"] + dims["front_overhang"] + clearance
    rear = dims["rear_overhang"] + clearance
    fl = point_offset(p, fwd, side, front, half_w)
    fr = point_offset(p, fwd, side, front, -half_w)
    rl = point_offset(p, fwd, side, -rear, half_w)
    rr = point_offset(p, fwd, side, -rear, -half_w)
    return fl, fr, rr, rl


def max_profile_grade(poses):
    if not poses:
        return 0.0
    return max(abs(p["grade"]) for p in poses)


def smooth_open_curve(points, start_tangent=None, end_tangent=None, fit_tol=None):
    if points is None or len(points) < 2:
        return None
    if len(points) == 2:
        return rg.LineCurve(points[0], points[1])
    if fit_tol is None:
        fit_tol = max(sc.doc.ModelAbsoluteTolerance * 2.0, metres_to_model(0.005))
    try:
        if start_tangent is not None and end_tangent is not None:
            c = rg.NurbsCurve.CreateFromFitPoints(points, fit_tol, 3, False, start_tangent, end_tangent)
            if c is not None:
                return c
    except Exception:
        pass
    try:
        return rg.Curve.CreateInterpolatedCurve(points, 3)
    except Exception:
        return rg.PolylineCurve(points)


def join_curve_segments(curves):
    curves = [c for c in curves if c is not None]
    if not curves:
        return None
    if len(curves) == 1:
        return curves[0]
    try:
        joined = rg.Curve.JoinCurves(curves, sc.doc.ModelAbsoluteTolerance * 2.0)
        if joined and len(joined) == 1:
            return joined[0]
    except Exception:
        pass
    pc = rg.PolyCurve()
    for c in curves:
        try:
            pc.Append(c)
        except Exception:
            pass
    return pc if pc.SegmentCount > 0 else curves[0]


def trajectory_curve(poses, point_getter, tangent_getter=None):
    """Create Line + fitted turn curve + Line geometry from sampled poses."""
    pin = []
    pturn = []
    pout = []
    turn_poses = []
    for pose in poses:
        p = point_getter(pose)
        if pose["phase"] == "in":
            pin.append(p)
        elif pose["phase"] == "turn":
            pturn.append(p)
            turn_poses.append(pose)
        else:
            pout.append(p)

    parts = []
    if pin and pturn:
        parts.append(rg.LineCurve(pin[0], pturn[0]))
    if len(pturn) >= 2:
        if tangent_getter is None:
            f0, _ = pose_frame(turn_poses[0])
            f1, _ = pose_frame(turn_poses[-1])
        else:
            f0 = tangent_getter(turn_poses[0])
            f1 = tangent_getter(turn_poses[-1])
        parts.append(smooth_open_curve(pturn, f0, f1))
    if pturn and pout:
        parts.append(rg.LineCurve(pturn[-1], pout[-1]))
    elif len(pout) >= 2:
        parts.append(rg.LineCurve(pout[0], pout[-1]))
    return join_curve_segments(parts)

def front_axle_tangent(pose):
    h = pose["heading"] + pose.get("steer", 0.0)
    g = pose.get("grade", 0.0)
    v = rg.Vector3d(math.cos(h), math.sin(h), g)
    if not v.Unitize():
        v = rg.Vector3d(math.cos(h), math.sin(h), 0.0)
    return v


def point_to_infinite_line_distance_xy(pt, line):
    dx = line.To.X - line.From.X
    dy = line.To.Y - line.From.Y
    L = math.hypot(dx, dy)
    if L <= 1e-12:
        return 1e99
    return abs(cross2(dx, dy, pt.X - line.From.X, pt.Y - line.From.Y)) / L


def _largest_closed_curve(curves):
    if not curves:
        return None
    best = None
    best_area = -1.0
    for c in curves:
        if c is None:
            continue
        try:
            if not c.IsClosed:
                continue
        except Exception:
            continue
        try:
            amp = rg.AreaMassProperties.Compute(c)
            if amp is None:
                continue
            area = abs(amp.Area)
        except Exception:
            continue
        if area > best_area:
            best_area = area
            best = c
    return best

def base_road_region_curve(turn, road_width, lead):
    """Planar L-shaped base carriageway using only the intended road arms.

    The finite arm length must cover the complete sampled maneuver. In older versions the
    rectangles could end before the vehicle run-in, producing a false local-widening result.
    """
    if road_width <= 0.0:
        return None
    half = road_width * 0.5
    ip = turn.get("road_intersection", turn["intersection"])
    d1 = turn.get("road_d_in", turn["d_in"])
    d2 = turn.get("road_d_out", turn["d_out"])
    tangent = float(turn.get("profile", {}).get("tangent_distance", 0.0))
    vehicle_length = float(turn.get("vehicle_length", metres_to_model(12.0)))
    extra = max(tangent + lead + vehicle_length, lead, metres_to_model(10.0))

    a1 = p2_add(ip, d1, -extra)
    b1 = ip
    a2 = ip
    b2 = p2_add(ip, d2, extra)
    r1 = planar_rectangle_about_segment(a1, b1, half)
    r2 = planar_rectangle_about_segment(a2, b2, half)
    try:
        unions = rg.Curve.CreateBooleanUnion(
            [r1, r2], max(sc.doc.ModelAbsoluteTolerance, metres_to_model(0.002))
        )
    except Exception:
        unions = None
    return _largest_closed_curve(unions)

def _curve_contains_xy(boundary, pt):
    if boundary is None:
        return False
    q = rg.Point3d(pt.X, pt.Y, 0.0)
    try:
        state = boundary.Contains(q, rg.Plane.WorldXY, max(sc.doc.ModelAbsoluteTolerance, metres_to_model(0.001)))
        return state != rg.PointContainment.Outside
    except Exception:
        return False


def _distance_to_curve_xy(boundary, pt):
    if boundary is None:
        return 1e99
    q = rg.Point3d(pt.X, pt.Y, 0.0)
    try:
        ok, t = boundary.ClosestPoint(q)
        if ok:
            cp = boundary.PointAt(t)
            return math.hypot(q.X - cp.X, q.Y - cp.Y)
    except Exception:
        pass
    return 1e99


def road_fit_check(poses, vehicle_key, clearance, road_width, turn, road_layout):
    """Report straight-width adequacy separately from local corner widening.

    V0.6 reported 2*max(distance to nearest infinite centreline) as a 'required road
    width'. That is not a meaningful road width at a corner and could produce values
    around 8 m for REN. Here we instead report:
      1) the straight swept width of the vehicle (+ clearance both sides), and
      2) whether the turn fits an L-shaped base carriageway of the entered width,
         including the maximum LOCAL widening required at the corner.
    """
    if road_width <= 0.0:
        return None

    dims = vehicle_dims_model(vehicle_key)
    straight_required = dims["width"] + 2.0 * clearance
    available_straight = road_width if road_layout == "single_lane" else road_width * 0.5
    straight_shortfall = max(0.0, straight_required - available_straight)

    lead = turn.get("lead", metres_to_model(10.0))
    base = base_road_region_curve(turn, road_width, lead)
    max_overrun = 0.0
    worst = None
    outside_count = 0

    if base is not None:
        for pose in poses:
            for q in clearance_corners(pose, dims, clearance):
                if not _curve_contains_xy(base, q):
                    outside_count += 1
                    d = _distance_to_curve_xy(base, q)
                    if d > max_overrun and d < 1e98:
                        max_overrun = d
                        worst = q

    turn_fits = (outside_count == 0)
    straight_fits = straight_shortfall <= sc.doc.ModelAbsoluteTolerance
    return {
        "fits": straight_fits and turn_fits,
        "straight_fits": straight_fits,
        "turn_fits": turn_fits,
        "straight_required_width": straight_required,
        "available_straight_width": available_straight,
        "straight_shortfall": straight_shortfall,
        "max_local_widening": max_overrun,
        "worst_point": worst,
        "base_region": base,
    }


def road_corridor_edge_curves(turn, road_width, lead, line_in, line_out):
    """Base carriageway edges used for the width check / optional output."""
    if road_width <= 0.0:
        return []
    half = road_width * 0.5
    ip = turn.get("road_intersection", turn["intersection"])
    d1 = turn.get("road_d_in", turn["d_in"])
    d2 = turn.get("road_d_out", turn["d_out"])
    n1 = left2(d1)
    n2 = left2(d2)
    start = p2_add(ip, d1, -lead)
    end = p2_add(ip, d2, lead)

    def make_leg(a, b, d, n, line):
        result = []
        for sgn in (-1.0, 1.0):
            aa = (a[0] + n[0] * half * sgn, a[1] + n[1] * half * sgn)
            bb = (b[0] + n[0] * half * sgn, b[1] + n[1] * half * sgn)
            pa = rg.Point3d(aa[0], aa[1], line_z_at_xy(line, a))
            pb = rg.Point3d(bb[0], bb[1], line_z_at_xy(line, b))
            result.append(rg.LineCurve(pa, pb))
        return result

    curves = []
    curves.extend(make_leg(start, ip, d1, n1, line_in))
    curves.extend(make_leg(ip, end, d2, n2, line_out))
    return curves


def planar_rectangle_about_segment(a, b, half_width):
    d = unit2(b[0] - a[0], b[1] - a[1])
    if d is None:
        return None
    n = left2(d)
    p1 = rg.Point3d(a[0] + n[0] * half_width, a[1] + n[1] * half_width, 0.0)
    p2 = rg.Point3d(b[0] + n[0] * half_width, b[1] + n[1] * half_width, 0.0)
    p3 = rg.Point3d(b[0] - n[0] * half_width, b[1] - n[1] * half_width, 0.0)
    p4 = rg.Point3d(a[0] - n[0] * half_width, a[1] - n[1] * half_width, 0.0)
    return rg.PolylineCurve([p1, p2, p3, p4, p1])


def planarized_closed_curve(curve, spacing):
    if curve is None:
        return None
    try:
        params = curve.DivideByLength(max(spacing, sc.doc.ModelAbsoluteTolerance * 5.0), True)
    except Exception:
        params = None
    pts = []
    if params is not None and len(params) >= 4:
        for t in params:
            q = curve.PointAt(t)
            pts.append(rg.Point3d(q.X, q.Y, 0.0))
    else:
        try:
            count = max(24, int(math.ceil(curve.GetLength() / max(spacing, 1e-6))))
            params = curve.DivideByCount(count, True)
            if params:
                for t in params:
                    q = curve.PointAt(t)
                    pts.append(rg.Point3d(q.X, q.Y, 0.0))
        except Exception:
            pass
    if len(pts) < 3:
        return None
    if pts[0].DistanceTo(pts[-1]) > sc.doc.ModelAbsoluteTolerance:
        pts.append(pts[0])
    return rg.PolylineCurve(pts)


def lift_closed_plan_curve(curve, poses):
    """Lift a planar road outline onto the longitudinal road profile."""
    if curve is None:
        return None
    seg = max(metres_to_model(0.12), sc.doc.ModelAbsoluteTolerance * 20.0)
    try:
        params = curve.DivideByLength(seg, True)
    except Exception:
        params = None
    if params is None or len(params) < 6:
        return curve
    pts = []
    for t in params:
        q = curve.PointAt(t)
        pts.append(rg.Point3d(q.X, q.Y, nearest_pose_z_for_xy(q.X, q.Y, poses)))
    if len(pts) >= 2 and pts[0].DistanceTo(pts[-1]) <= seg * 0.75:
        pts.pop()
    try:
        fit_tol = max(sc.doc.ModelAbsoluteTolerance * 2.0, metres_to_model(0.01))
        fitted = rg.NurbsCurve.CreateFromFitPoints(pts, fit_tol, True)
        if fitted is not None:
            return fitted
    except Exception:
        pass
    if pts:
        pts.append(pts[0])
        return rg.PolylineCurve(pts)
    return curve


def required_road_outline_curve(turn, poses, envelope_curve, road_width, lead, line_in, line_out):
    """Suggested pavement outline = intended L-shaped road arms UNION exact clearance envelope."""
    if road_width <= 0.0 or envelope_curve is None:
        return None

    ip = turn.get("road_intersection", turn["intersection"])
    base = base_road_region_curve(turn, road_width, lead)
    env2d = planarized_closed_curve(
        envelope_curve, max(metres_to_model(0.04), sc.doc.ModelAbsoluteTolerance * 8.0)
    )
    inputs = [c for c in (base, env2d) if c is not None]
    if len(inputs) < 2:
        return None
    try:
        unions = rg.Curve.CreateBooleanUnion(
            inputs, max(sc.doc.ModelAbsoluteTolerance, metres_to_model(0.002))
        )
    except Exception:
        unions = None
    boundary = _largest_closed_curve(unions)
    if boundary is None:
        return None

    if abs(turn.get("g1", 0.0)) < 1e-8 and abs(turn.get("g2", 0.0)) < 1e-8:
        z = 0.5 * (line_z_at_xy(line_in, ip) + line_z_at_xy(line_out, ip))
        moved = boundary.DuplicateCurve()
        moved.Transform(rg.Transform.Translation(0.0, 0.0, z))
        return moved
    return lift_closed_plan_curve(boundary, poses)

def nearest_pose_z_for_xy(x, y, poses):
    best = None
    best_d2 = 1e300
    for pose in poses:
        p = pose["p"]
        dx = x - p.X
        dy = y - p.Y
        d2 = dx * dx + dy * dy
        if d2 < best_d2:
            best_d2 = d2
            best = pose
    if best is None:
        return 0.0
    p = best["p"]
    h = best["heading"]
    along = (x - p.X) * math.cos(h) + (y - p.Y) * math.sin(h)
    return p.Z + best["grade"] * along



def _boolean_union_batched(curves, tol, batch_size=32):
    """Robustly union many overlapping closed planar curves.

    Rhino's one-shot Curve.CreateBooleanUnion can return None for hundreds of nearly coincident
    footprint rectangles. Reduce them in batches first, then union the reduced set.
    """
    work = [c for c in curves if c is not None]
    if not work:
        return None
    if len(work) == 1:
        return work
    # First pass: chunks.
    reduced = []
    for i in range(0, len(work), batch_size):
        chunk = work[i:i+batch_size]
        try:
            u = rg.Curve.CreateBooleanUnion(chunk, tol)
        except Exception:
            u = None
        if u:
            reduced.extend([c for c in u if c is not None])
        else:
            # Pairwise fallback within the chunk.
            acc = [chunk[0]]
            for c in chunk[1:]:
                try:
                    u2 = rg.Curve.CreateBooleanUnion(acc + [c], tol)
                except Exception:
                    u2 = None
                if u2:
                    acc = list(u2)
                else:
                    acc.append(c)
            reduced.extend(acc)
    # Repeatedly reduce until stable/small.
    work = reduced
    for _ in range(8):
        if len(work) <= 1:
            break
        try:
            u = rg.Curve.CreateBooleanUnion(work, tol)
        except Exception:
            u = None
        if u:
            return list(u)
        if len(work) <= batch_size:
            break
        nxt = []
        for i in range(0, len(work), batch_size):
            chunk = work[i:i+batch_size]
            try:
                u = rg.Curve.CreateBooleanUnion(chunk, tol)
            except Exception:
                u = None
            nxt.extend(list(u) if u else chunk)
        if len(nxt) >= len(work):
            break
        work = nxt
    return work

def swept_union_curve(poses, vehicle_key, clearance, preview=False):
    """Return the exact sampled swept-footprint union boundary.

    V0.9 globally fitted a closed NURBS through this boundary. That looked smooth, but the fit
    could balloon well outside the true swept area (or cut inside it). V0.11 keeps the Boolean
    boundary itself. Sampling is finer for commit than preview, but the geometric check never
    uses a fitted/smoothed envelope.
    """
    if not poses:
        return None
    dims = vehicle_dims_model(vehicle_key)
    spacing_m = 0.08 if preview else 0.025
    sample_spacing = max(metres_to_model(spacing_m), sc.doc.ModelAbsoluteTolerance * 8.0)
    footprints = []
    last_station = -1e99
    selected = []
    for pose in poses:
        if pose["station"] - last_station >= sample_spacing:
            selected.append(pose)
            last_station = pose["station"]
    if not selected or selected[-1] is not poses[-1]:
        selected.append(poses[-1])

    for pose in selected:
        fl, fr, rr, rl = clearance_corners(pose, dims, clearance)
        pts = [
            rg.Point3d(fl.X, fl.Y, 0.0),
            rg.Point3d(fr.X, fr.Y, 0.0),
            rg.Point3d(rr.X, rr.Y, 0.0),
            rg.Point3d(rl.X, rl.Y, 0.0),
            rg.Point3d(fl.X, fl.Y, 0.0),
        ]
        footprints.append(rg.PolylineCurve(pts))

    bool_tol = max(sc.doc.ModelAbsoluteTolerance, metres_to_model(0.001))
    unions = _boolean_union_batched(footprints, bool_tol, 28 if preview else 40)
    # Final merge if the batched reducer left multiple overlapping regions.
    if unions and len(unions) > 1:
        try:
            merged = rg.Curve.CreateBooleanUnion(unions, bool_tol)
            if merged:
                unions = merged
        except Exception:
            pass
    boundary = _largest_closed_curve(unions)
    if boundary is None:
        return None

    # Flat case: preserve Rhino's exact Boolean boundary rather than refitting it.
    if max(abs(p.get("grade", 0.0)) for p in poses) < 1e-9:
        z = sum(p["p"].Z for p in poses) / float(len(poses))
        out = boundary.DuplicateCurve()
        out.Transform(rg.Transform.Translation(0.0, 0.0, z))
        return out

    # Sloped case: plan swept-area logic remains exact in XY; the 3D representation is a
    # sampled lift because a single planar closed curve cannot represent varying elevation.
    seg = max(metres_to_model(0.05 if preview else 0.02), sc.doc.ModelAbsoluteTolerance * 8.0)
    try:
        params = boundary.DivideByLength(seg, True)
    except Exception:
        params = None
    if params is None or len(params) < 4:
        return boundary
    pts = []
    for t in params:
        q = boundary.PointAt(t)
        pts.append(rg.Point3d(q.X, q.Y, nearest_pose_z_for_xy(q.X, q.Y, poses)))
    if pts and pts[0].DistanceTo(pts[-1]) > sc.doc.ModelAbsoluteTolerance:
        pts.append(pts[0])
    return rg.PolylineCurve(pts)

def build_geometry(poses, turn, vehicle_key, clearance, road_width, outputs, position_spacing, line_in, line_out, preview=False):
    dims = vehicle_dims_model(vehicle_key)
    turn["vehicle_length"] = dims["length"]

    rear_curve = trajectory_curve(poses, lambda pose: pose["p"])
    if turn.get("template_model", False):
        front_curve = exact_front_template_curve(turn, turn.get("lead", metres_to_model(10.0)), poses)
    else:
        front_curve = trajectory_curve(
            poses,
            lambda pose: point_offset(pose["p"], pose_frame(pose)[0], pose_frame(pose)[1], dims["wheelbase"], 0.0),
            tangent_getter=front_axle_tangent,
        )

    need_body = outputs.get("envelope", False)
    need_clearance = outputs.get("clearance_envelope", False) or outputs.get("road_outline", False)
    body_envelope = swept_union_curve(poses, vehicle_key, 0.0, preview) if need_body else None
    clearance_envelope = swept_union_curve(poses, vehicle_key, clearance, preview) if need_clearance else None
    geometry_warnings = []
    if need_body and body_envelope is None:
        geometry_warnings.append("Swept-envelope Boolean failed; reduce model tolerance or try Create again.")
    if need_clearance and clearance_envelope is None:
        geometry_warnings.append("Clearance-envelope Boolean failed.")

    road_edges = road_corridor_edge_curves(
        turn, road_width, turn.get("lead", metres_to_model(10.0)), line_in, line_out
    ) if (road_width > 0.0 and outputs.get("road_edges", False)) else []

    road_outline = required_road_outline_curve(
        turn, poses, clearance_envelope, road_width,
        turn.get("lead", metres_to_model(10.0)), line_in, line_out
    ) if (road_width > 0.0 and outputs.get("road_outline", False) and clearance_envelope is not None) else None

    body_positions = []
    if outputs.get("positions", False):
        half_w = dims["width"] * 0.5
        body_front = dims["wheelbase"] + dims["front_overhang"]
        body_rear = dims["rear_overhang"]
        next_position_station = 0.0
        for pose in poses:
            if pose["station"] + 1e-9 >= next_position_station:
                p = pose["p"]
                fwd, side = pose_frame(pose)
                bfl = point_offset(p, fwd, side, body_front, half_w)
                bfr = point_offset(p, fwd, side, body_front, -half_w)
                brl = point_offset(p, fwd, side, -body_rear, half_w)
                brr = point_offset(p, fwd, side, -body_rear, -half_w)
                body_positions.append(rg.PolylineCurve([bfl, bfr, brr, brl, bfl]))
                next_position_station += max(position_spacing, sc.doc.ModelAbsoluteTolerance * 10.0)

    fit = road_fit_check(poses, vehicle_key, clearance, road_width, turn, turn.get("road_layout", "single_lane"))

    return {
        "reference": [rear_curve] if rear_curve is not None else [],
        # Front axle only: rear axle is the separately toggled reference curve.
        "axles": [front_curve] if front_curve is not None else [],
        "envelope": [body_envelope] if (body_envelope is not None and outputs.get("envelope", False)) else [],
        "clearance_envelope": [clearance_envelope] if (clearance_envelope is not None and outputs.get("clearance_envelope", False)) else [],
        "road_edges": road_edges,
        "road_outline": [road_outline] if road_outline is not None else [],
        "positions": body_positions,
        "road_fit": fit,
        "warnings": geometry_warnings,
    }

# -----------------------------------------------------------------------------
# Display conduit
# -----------------------------------------------------------------------------
class PreviewConduit(rdisp.DisplayConduit):
    def __init__(self):
        rdisp.DisplayConduit.__init__(self)
        self.geom = None
        self.outputs = None
        self.turn = None
        self._line_in = None
        self._line_out = None

    def set_data(self, geom, outputs, turn):
        self.geom = geom
        self.outputs = outputs
        self.turn = turn

    def set_input_lines(self, line_in, line_out):
        self._line_in = line_in
        self._line_out = line_out

    def _draw_curve(self, display, curve, color, thickness=2):
        if curve is not None:
            display.DrawCurve(curve, color, thickness)

    def _all_preview_geometry(self):
        if not self.geom:
            return []
        result = []
        for key in ("reference", "axles", "envelope", "clearance_envelope", "road_edges", "road_outline", "positions"):
            result.extend([g for g in self.geom.get(key, []) if g is not None])
        return result

    def CalculateBoundingBox(self, e):
        try:
            items = self._all_preview_geometry()
            if not items:
                return
            bbox = rg.BoundingBox.Empty
            for g in items:
                try:
                    bbox.Union(g.GetBoundingBox(True))
                except Exception:
                    pass
            fit = self.geom.get("road_fit") if self.geom else None
            if fit and fit.get("worst_point") is not None:
                bbox.Union(fit["worst_point"])
            if bbox.IsValid:
                e.IncludeBoundingBox(bbox)
        except Exception:
            pass

    def PostDrawObjects(self, e):
        try:
            if not self.geom or not self.outputs:
                return

            if self.outputs.get("reference", False):
                for c in self.geom.get("reference", []):
                    self._draw_curve(e.Display, c, sd.Color.White, 2)

            if self.outputs.get("axles", False):
                for c in self.geom.get("axles", []):
                    self._draw_curve(e.Display, c, sd.Color.DeepSkyBlue, 1)

            if self.outputs.get("envelope", False):
                for c in self.geom.get("envelope", []):
                    self._draw_curve(e.Display, c, sd.Color.OrangeRed, 2)

            if self.outputs.get("clearance_envelope", False):
                for c in self.geom.get("clearance_envelope", []):
                    self._draw_curve(e.Display, c, sd.Color.MediumPurple, 1)

            if self.outputs.get("road_edges", False):
                for c in self.geom.get("road_edges", []):
                    self._draw_curve(e.Display, c, sd.Color.Gold, 1)

            if self.outputs.get("road_outline", False):
                for c in self.geom.get("road_outline", []):
                    self._draw_curve(e.Display, c, sd.Color.LimeGreen, 2)

            if self.outputs.get("positions", False):
                for c in self.geom.get("positions", []):
                    self._draw_curve(e.Display, c, sd.Color.FromArgb(150, 190, 190, 190), 1)

            fit = self.geom.get("road_fit")
            if fit and not fit.get("fits", True) and fit.get("worst_point") is not None:
                e.Display.DrawPoint(fit["worst_point"], rdisp.PointStyle.ControlPoint, 5, sd.Color.Red)

            if self.turn and self._line_in is not None and self._line_out is not None:
                ip = self.turn.get("road_intersection", self.turn["intersection"])
                z = 0.5 * (line_z_at_xy(self._line_in, ip) + line_z_at_xy(self._line_out, ip))
                e.Display.DrawPoint(rg.Point3d(ip[0], ip[1], z), rdisp.PointStyle.ControlPoint, 3, sd.Color.Gray)
        except Exception as ex:
            # Conduit exceptions repeat on every redraw; swallow them here and report once to console.
            try:
                print("VehicleTurn preview draw error: {0}".format(ex))
            except Exception:
                pass

# -----------------------------------------------------------------------------
# Commit ordinary Rhino curves
# -----------------------------------------------------------------------------
def commit_geometry(geom, outputs, vehicle_key="Vehicle", mode=""):
    """Commit ordinary Rhino curves on the current layer as one undo step and one group."""
    ids = []
    layer_index = sc.doc.Layers.CurrentLayerIndex
    label_map = {
        "reference": "RearAxlePath",
        "axles": "FrontAxlePath",
        "envelope": "SweptEnvelope",
        "clearance_envelope": "ClearanceEnvelope",
        "road_edges": "RoadEdge",
        "road_outline": "SuggestedPavement",
        "positions": "VehiclePosition",
    }
    key_map = [
        ("reference", "reference"),
        ("axles", "axles"),
        ("envelope", "envelope"),
        ("clearance_envelope", "clearance_envelope"),
        ("road_edges", "road_edges"),
        ("road_outline", "road_outline"),
        ("positions", "positions"),
    ]

    undo = sc.doc.BeginUndoRecord("VehicleTurn")
    try:
        counters = {}
        for output_key, geom_key in key_map:
            if not outputs.get(output_key, False):
                continue
            for curve in geom.get(geom_key, []):
                if curve is None:
                    continue
                counters[geom_key] = counters.get(geom_key, 0) + 1
                attrs = rd.ObjectAttributes()
                attrs.LayerIndex = layer_index
                attrs.Name = "VehicleTurn_{0}_{1}_{2}_{3:02d}".format(
                    vehicle_key, mode, label_map.get(geom_key, geom_key), counters[geom_key]
                )
                oid = sc.doc.Objects.AddCurve(curve, attrs)
                if oid:
                    ids.append(oid)

        if ids:
            try:
                group_name = "VehicleTurn_{0}_{1}".format(vehicle_key, mode)
                gi = sc.doc.Groups.Add(group_name)
                if gi >= 0:
                    sc.doc.Groups.AddToGroup(gi, ids)
            except Exception:
                pass
    finally:
        if undo >= 0:
            sc.doc.EndUndoRecord(undo)
    sc.doc.Views.Redraw()
    return ids

# -----------------------------------------------------------------------------
# Eto dialog
# -----------------------------------------------------------------------------
def make_label(text, width=None):
    # Rhino 8 CPython/pythonnet does not support IronPython-style
    # constructor/property initialization for Eto controls.
    lbl = ef.Label()
    lbl.Text = str(text)
    if width is not None:
        lbl.Width = width
    lbl.VerticalAlignment = ef.VerticalAlignment.Center
    return lbl


def make_stepper(value, minimum=0.0, maximum=10000.0, increment=0.1, decimals=2):
    s = ef.NumericStepper()
    # DecimalPlaces and limits MUST be set before Value in Eto.
    s.DecimalPlaces = decimals
    s.MinValue = minimum
    s.MaxValue = maximum
    s.Increment = increment
    try:
        s.Wrap = False
    except Exception:
        pass
    s.Value = clamp(value, minimum, maximum)
    s.Width = 105
    return s


class VehicleTurnDialog(ef.Dialog[bool]):
    def __init__(self, line_in, pick_in, line_out, pick_out):
        super().__init__()
        self.Title = "Vehicle Turn"
        self.Padding = ed.Padding(12)
        self.Resizable = False
        self.MinimumSize = ed.Size(650, 600)

        self.line_in = line_in
        self.pick_in = pick_in
        self.line_out = line_out
        self.pick_out = pick_out
        self.latest_geom = None
        self.latest_turn = None
        self.latest_poses = None
        self.accepted = False
        self._updating = True

        self.conduit = PreviewConduit()
        self.conduit.set_input_lines(self.line_in, self.line_out)
        self.conduit.Enabled = True

        # Remembered settings are stored in USER-FACING METRES.
        vehicle_idx = sticky_int("vehicle_idx", 1, 0, len(VEHICLE_KEYS) - 1)
        mode_idx = sticky_int("mode_idx", 1, 0, 1)
        road_layout_idx = sticky_int("road_layout_idx", 0, 0, 1)
        steering_model_idx = sticky_int("steering_model_idx", 0, 0, 1)
        vehicle_idx = int(clamp(vehicle_idx, 0, len(VEHICLE_KEYS) - 1))
        mode_idx = int(clamp(mode_idx, 0, 1))
        road_layout_idx = int(clamp(road_layout_idx, 0, 1))
        steering_model_idx = int(clamp(steering_model_idx, 0, 1))

        vehicle_key = VEHICLE_KEYS[vehicle_idx]
        mode = ["A", "B"][mode_idx]
        min_r_m = min_front_radius_m(vehicle_key, mode)

        radius_m = sticky_float("radius_m", min_r_m, 0.10, 500.0)
        radius_m = max(radius_m, min_r_m)
        clearance_m = sticky_float("clearance_m", 0.30, 0.0, 10.0)
        road_width_m = sticky_float("road_width_m", 6.00, 0.0, 50.0)
        lead_m = sticky_float(
            "lead_m",
            VEHICLES[vehicle_key]["front_overhang_m"] + VEHICLES[vehicle_key]["wheelbase_m"] + VEHICLES[vehicle_key]["rear_overhang_m"] + 3.0,
            0.5, 100.0,
        )
        spacing_m = sticky_float("spacing_m", 2.0, 0.25, 50.0)
        lock_to_lock_s = sticky_float("lock_to_lock_s", default_lock_to_lock_s(), 1.0, 20.0)

        # Vehicle / mode
        self.vehicle = ef.DropDown()
        self.vehicle.DataStore = [VEHICLES[k]["label"] for k in VEHICLE_KEYS]
        self.vehicle.SelectedIndex = vehicle_idx

        self.mode = ef.DropDown()
        self.mode.DataStore = ["A - free movement / 15 km/h", "B - constrained / 5 km/h"]
        self.mode.SelectedIndex = mode_idx

        self.road_layout = ef.DropDown()
        self.road_layout.DataStore = [
            "Two-way road - right-hand traffic",
            "Single-lane / shared road",
        ]
        self.road_layout.SelectedIndex = road_layout_idx

        self.steering_model = ef.DropDown()
        self.steering_model.DataStore = [
            "Vej template - front axle tangent / arc / tangent",
            "Experimental SmartPath - continuous rear-axle steering",
        ]
        self.steering_model.SelectedIndex = steering_model_idx

        # Geometry values
        self.radius = make_stepper(radius_m, 0.10, 500.0, 0.25, 2)
        self.lock_to_lock = make_stepper(lock_to_lock_s, 1.0, 20.0, 0.25, 2)
        self.clearance = make_stepper(clearance_m, 0.0, 10.0, 0.05, 2)
        self.road_width = make_stepper(road_width_m, 0.0, 50.0, 0.25, 2)
        self.lead = make_stepper(lead_m, 0.5, 100.0, 0.5, 1)
        self.spacing = make_stepper(spacing_m, 0.25, 50.0, 0.25, 2)

        self.minimum_radius = make_label("")
        self.turn_info = make_label("")
        self.grade_info = make_label("")
        self.road_check = make_label("")
        self.road_check.Wrap = ef.WrapMode.Word
        self.road_check.Width = 560
        self.warning = make_label("")
        self.warning.Wrap = ef.WrapMode.Word
        self.warning.Width = 390

        self.use_min = ef.Button()
        self.use_min.Text = "Use minimum radius"
        self.repick = ef.Button()
        self.repick.Text = "Repick centrelines"

        # Outputs
        self.out_envelope = ef.CheckBox()
        self.out_envelope.Text = "Vehicle swept envelope (no clearance)"
        self.out_envelope.Checked = sticky_bool("out_envelope", True)
        self.out_clearance = ef.CheckBox()
        self.out_clearance.Text = "Clearance envelope"
        self.out_clearance.Checked = sticky_bool("out_clearance", False)
        self.out_axles = ef.CheckBox()
        self.out_axles.Text = "Axle paths"
        self.out_axles.Checked = sticky_bool("out_axles", False)
        self.out_positions = ef.CheckBox()
        self.out_positions.Text = "Vehicle positions"
        self.out_positions.Checked = sticky_bool("out_positions", False)
        self.out_road_edges = ef.CheckBox()
        self.out_road_edges.Text = "Base road edges"
        self.out_road_edges.Checked = sticky_bool("out_road_edges", False)
        self.out_road_outline = ef.CheckBox()
        self.out_road_outline.Text = "Suggested pavement outline"
        self.out_road_outline.Checked = sticky_bool("out_road_outline", False)
        self.out_reference = ef.CheckBox()
        self.out_reference.Text = "Rear-axle path"
        self.out_reference.Checked = sticky_bool("out_reference", True)

        # Buttons. Default + Abort are required by ShowSemiModal.
        self.DefaultButton = ef.Button()
        self.DefaultButton.Text = "Create"
        self.AbortButton = ef.Button()
        self.AbortButton.Text = "Cancel"

        # Event wiring
        self.vehicle.SelectedIndexChanged += self.on_vehicle_or_mode_changed
        self.mode.SelectedIndexChanged += self.on_vehicle_or_mode_changed
        self.road_layout.SelectedIndexChanged += self.on_value_changed
        self.steering_model.SelectedIndexChanged += self.on_steering_model_changed
        self.radius.ValueChanged += self.on_value_changed
        self.lock_to_lock.ValueChanged += self.on_value_changed
        self.clearance.ValueChanged += self.on_value_changed
        self.road_width.ValueChanged += self.on_value_changed
        self.lead.ValueChanged += self.on_value_changed
        self.spacing.ValueChanged += self.on_value_changed
        self.out_envelope.CheckedChanged += self.on_value_changed
        self.out_clearance.CheckedChanged += self.on_value_changed
        self.out_axles.CheckedChanged += self.on_value_changed
        self.out_positions.CheckedChanged += self.on_value_changed
        self.out_road_edges.CheckedChanged += self.on_value_changed
        self.out_road_outline.CheckedChanged += self.on_value_changed
        self.out_reference.CheckedChanged += self.on_value_changed
        self.use_min.Click += self.on_use_minimum
        self.repick.Click += self.on_repick
        self.DefaultButton.Click += self.on_create
        self.AbortButton.Click += self.on_cancel
        self.Closed += self.on_closed

        # Layout
        layout = ef.DynamicLayout()
        layout.Spacing = ed.Size(8, 6)

        layout.AddRow(make_label("Vehicle", 145), self.vehicle)
        layout.AddRow(make_label("Driving mode", 145), self.mode)
        layout.AddRow(make_label("Road setup", 145), self.road_layout)
        layout.AddRow(make_label("Steering model", 145), self.steering_model)
        layout.AddRow(None)

        layout.AddRow(make_label("Path radius", 145), self.radius, make_label("m"))
        layout.AddRow(make_label("", 145), self.minimum_radius)
        layout.AddRow(make_label("Lock-to-lock time", 145), self.lock_to_lock, make_label("s"))
        layout.AddRow(make_label("Clearance", 145), self.clearance, make_label("m"))
        layout.AddRow(make_label("Total road width", 145), self.road_width, make_label("m"))
        layout.AddRow(make_label("Road check", 145), self.road_check)
        layout.AddRow(make_label("Run in / out", 145), self.lead, make_label("m"))
        layout.AddRow(make_label("Vehicle spacing", 145), self.spacing, make_label("m"))
        layout.AddRow(make_label("", 145), self.use_min, self.repick)
        layout.AddRow(None)

        layout.AddRow(make_label("Output", 145), self.out_envelope, self.out_clearance)
        layout.AddRow(make_label("", 145), self.out_reference, self.out_axles)
        layout.AddRow(make_label("", 145), self.out_positions, self.out_road_edges)
        layout.AddRow(make_label("", 145), self.out_road_outline)
        layout.AddRow(None)

        layout.AddRow(make_label("Turn", 145), self.turn_info)
        layout.AddRow(make_label("Grades", 145), self.grade_info)
        layout.AddRow(make_label("", 145), self.warning)
        layout.AddRow(None)
        layout.AddRow(None, self.DefaultButton, self.AbortButton)

        self.Content = layout
        self.lock_to_lock.Enabled = (self.steering_model.SelectedIndex == 1)
        self._updating = False
        self.update_preview()

    # -------------------------------
    # State
    # -------------------------------
    def vehicle_key(self):
        idx = int(clamp(self.vehicle.SelectedIndex, 0, len(VEHICLE_KEYS) - 1))
        return VEHICLE_KEYS[idx]

    def mode_key(self):
        return "A" if self.mode.SelectedIndex == 0 else "B"

    def road_layout_key(self):
        return "two_way" if self.road_layout.SelectedIndex == 0 else "single_lane"

    def steering_model_key(self):
        return "template" if self.steering_model.SelectedIndex == 0 else "continuous"

    def outputs(self):
        return {
            "envelope": bool(self.out_envelope.Checked),
            "clearance_envelope": bool(self.out_clearance.Checked),
            "axles": bool(self.out_axles.Checked),
            "positions": bool(self.out_positions.Checked),
            "road_edges": bool(self.out_road_edges.Checked),
            "road_outline": bool(self.out_road_outline.Checked),
            "reference": bool(self.out_reference.Checked),
        }

    def on_vehicle_or_mode_changed(self, sender, e):
        if self._updating:
            return
        minimum = min_front_radius_m(self.vehicle_key(), self.mode_key()) if self.steering_model_key() == "template" else min_rear_radius_m(self.vehicle_key(), self.mode_key())
        self._updating = True
        if self.radius.Value < minimum:
            self.radius.Value = minimum
        self.lock_to_lock.Value = default_lock_to_lock_s()
        # Mode B defaults to the minimum-space steering model; Mode A to continuous steering.
        self.steering_model.SelectedIndex = 0
        self.lock_to_lock.Enabled = (self.steering_model.SelectedIndex == 1)
        self._updating = False
        self.update_preview()

    def on_steering_model_changed(self, sender, e):
        if self._updating:
            return
        self.lock_to_lock.Enabled = (self.steering_model.SelectedIndex == 1)
        minimum = min_front_radius_m(self.vehicle_key(), self.mode_key()) if self.steering_model_key() == "template" else min_rear_radius_m(self.vehicle_key(), self.mode_key())
        self._updating = True
        if self.radius.Value < minimum:
            self.radius.Value = minimum
        self._updating = False
        self.update_preview()

    def on_value_changed(self, sender, e):
        if not self._updating:
            self.update_preview()

    def on_use_minimum(self, sender, e):
        self._updating = True
        self.radius.Value = min_front_radius_m(self.vehicle_key(), self.mode_key()) if self.steering_model_key() == "template" else min_rear_radius_m(self.vehicle_key(), self.mode_key())
        self._updating = False
        self.update_preview()

    # -------------------------------
    # Repick while semi-modal
    # -------------------------------
    def on_repick(self, sender, e):
        try:
            EtoExtensions.PushPickButton(self, self.do_repick)
        except Exception as ex:
            print("VehicleTurn repick could not start: {0}".format(ex))

    def do_repick(self, sender, e):
        ref_in, line_in, pick_in = get_linear_curve("Select INCOMING centreline (pick approach side)")
        if line_in is None:
            return
        ref_out, line_out, pick_out = get_linear_curve("Select OUTGOING centreline (pick departure side)")
        if line_out is None:
            return
        self.line_in = line_in
        self.pick_in = pick_in
        self.line_out = line_out
        self.pick_out = pick_out
        self.conduit.set_input_lines(line_in, line_out)
        self.update_preview()

    # -------------------------------
    # Preview
    # -------------------------------
    def update_preview(self):
        if self._updating:
            return
        try:
            return self._update_preview_impl()
        except Exception as ex:
            self.latest_geom = None
            self.latest_turn = None
            self.latest_poses = None
            try:
                self.conduit.set_data(None, None, None)
                self.warning.Text = "Preview error: {0}".format(ex)
                self.road_check.Text = "-"
                sc.doc.Views.Redraw()
            except Exception:
                pass
            print("VehicleTurn preview error: {0}".format(ex))

    def _update_preview_impl(self):
        if self._updating:
            return

        vk = self.vehicle_key()
        mode = self.mode_key()
        min_r_m = min_front_radius_m(vk, mode) if self.steering_model_key() == "template" else min_rear_radius_m(vk, mode)
        r_m = float(self.radius.Value)

        # Clamp without making normal typing frustrating.
        if r_m < min_r_m:
            self._updating = True
            self.radius.Value = min_r_m
            self._updating = False
            r_m = min_r_m

        r = metres_to_model(r_m)
        lock_to_lock_s = float(self.lock_to_lock.Value)
        clearance = metres_to_model(float(self.clearance.Value))
        road_width = metres_to_model(float(self.road_width.Value))
        lead = metres_to_model(float(self.lead.Value))
        position_spacing = metres_to_model(float(self.spacing.Value))

        path_data, err = make_vehicle_path_inputs(
            self.line_in, self.pick_in, self.line_out, self.pick_out, road_width, self.road_layout_key()
        )
        if path_data is None:
            turn = None
        else:
            if self.steering_model_key() == "template":
                turn, err = solve_front_template(
                    path_data["path_line_in"], path_data["path_pick_in"],
                    path_data["path_line_out"], path_data["path_pick_out"],
                    r, vk, mode
                )
            else:
                turn, err = solve_turn(
                    path_data["path_line_in"], path_data["path_pick_in"],
                    path_data["path_line_out"], path_data["path_pick_out"],
                    r, lock_to_lock_s, vk, mode, "continuous"
                )
            if turn is not None:
                # Preserve both concepts: the vehicle path is lane-aware, while road checks/output
                # are always referenced to the original selected road centrelines.
                turn["road_intersection"] = path_data["road_intersection"]
                turn["road_d_in"] = path_data["road_d_in"]
                turn["road_d_out"] = path_data["road_d_out"]
                turn["lane_offset"] = path_data["lane_offset"]
                turn["road_layout"] = self.road_layout_key()

        if turn is None:
            self.latest_geom = None
            self.latest_turn = None
            self.latest_poses = None
            self.conduit.set_data(None, None, None)
            self.minimum_radius.Text = "Minimum: {0:.2f} m".format(min_r_m)
            self.turn_info.Text = "-"
            self.grade_info.Text = "-"
            self.road_check.Text = "-"
            self.warning.Text = err
            sc.doc.Views.Redraw()
            return

        # Fine enough for a smooth preview but still responsive in Eto.
        step = metres_to_model(0.15)
        turn["lead"] = lead
        poses = sample_front_template(turn, lead, step, vk) if self.steering_model_key() == "template" else sample_turn(turn, lead, step)
        geom = build_geometry(
            poses,
            turn,
            vk,
            clearance,
            road_width,
            self.outputs(),
            position_spacing,
            self.line_in,
            self.line_out,
            preview=True,
        )

        self.latest_geom = geom
        self.latest_turn = turn
        self.latest_poses = poses
        self.conduit.set_data(geom, self.outputs(), turn)

        dims = vehicle_dims_model(vk)
        if self.steering_model_key() == "template":
            steer_used = math.degrees(math.asin(clamp(dims["wheelbase"] / r, -1.0, 1.0)))
            rear_r_m = model_to_metres(turn["profile"].get("rear_steady_radius", 0.0))
            self.minimum_radius.Text = "Minimum front-axle R: {0:.2f} m   |   R set: {1:.2f} m   |   lock equivalent: {2:.1f}° / {3:.1f}°   |   peak in this turn: {4:.1f}°   |   steady rear R: {5:.2f} m".format(
                min_r_m, r_m, steer_used, max_steer_deg(vk, mode), turn["profile"].get("steer_peak_deg", steer_used), rear_r_m
            )
        else:
            steer_used = math.degrees(math.atan(dims["wheelbase"] / r))
            actual_r_m = model_to_metres(turn["profile"]["actual_radius"])
            self.minimum_radius.Text = "Minimum rear-axle R: {0:.2f} m   |   requested steering: {1:.1f}° / {2:.1f}°   |   peak used: {3:.1f}°   |   actual R: {4:.2f} m".format(
                min_r_m, steer_used, max_steer_deg(vk, mode), turn["profile"].get("steer_peak_deg", steer_used), actual_r_m
            )
        lane_offset_m = model_to_metres(abs(turn.get("lane_offset", 0.0)))
        if self.road_layout_key() == "two_way":
            path_note = "lane centre {0:.2f} m right of CL".format(lane_offset_m)
        else:
            path_note = "vehicle path on road CL"
        if self.steering_model_key() == "template":
            steering_note = "Vej/standard template; FRONT axle follows tangent + arc + tangent"
        else:
            steering_note = "experimental continuous rear-axle steer; ramp {0:.2f} m / {1:.2f} s".format(
                model_to_metres(turn["profile"]["ramp_len"]), turn["profile"]["ramp_time"]
            )
        self.turn_info.Text = "{0:.1f}° {1} turn   |   {2}   |   {3:.0f} km/h   |   {4}".format(
            math.degrees(turn["angle"]),
            "left" if turn["turn_sign"] > 0 else "right",
            path_note,
            MODE_SPEED_KMH[mode],
            steering_note,
        )
        self.grade_info.Text = "in {0:+.1f}%   out {1:+.1f}%   max {2:.1f}%".format(
            turn["g1"] * 100.0,
            turn["g2"] * 100.0,
            max_profile_grade(poses) * 100.0,
        )
        fit = geom.get("road_fit")
        if fit is None:
            self.road_check.Text = "Off (set road width above 0 m)"
        else:
            req = model_to_metres(fit["straight_required_width"])
            avail = model_to_metres(fit["available_straight_width"])
            widen = model_to_metres(fit["max_local_widening"])
            if fit["straight_fits"]:
                straight_txt = "straight FITS ({0:.2f} m needed / {1:.2f} m available)".format(req, avail)
            else:
                short = model_to_metres(fit["straight_shortfall"])
                straight_txt = "straight TOO NARROW by {0:.2f} m ({1:.2f} / {2:.2f} m)".format(short, req, avail)
            if fit["turn_fits"]:
                turn_txt = "turn FITS base road"
            else:
                turn_txt = "turn needs local widening up to {0:.2f} m".format(widen)
            self.road_check.Text = straight_txt + "   |   " + turn_txt
        if self.road_layout_key() == "two_way":
            setup_note = "Two-way mode assumes equal lanes and right-hand traffic; the nominal template path runs at road width / 4 to the right of each centreline. "
        else:
            setup_note = "Single-lane mode uses the selected road centreline as the nominal template tangent. "
        warn_text = (
            setup_note +
            "Vej template mode prescribes the FRONT steering axle as tangent + circular arc + tangent, matching AutoTURN's standard-template radius definition; the body/rear axle offtrack from that path. "
            "The swept envelope is built from sampled vehicle footprints with a batched Rhino Boolean and excludes design clearance. Suggested pavement outline is separate. "
            "For direct Vej comparison use Vej template, Mode B, Use minimum radius, clearance 0.00, and compare only the orange Vehicle swept envelope. "
            "Inputs may stop short or extend through the junction; originals are never changed."
        )
        if geom.get("warnings"):
            warn_text = "WARNING: " + " ".join(geom.get("warnings")) + "  " + warn_text
        self.warning.Text = warn_text
        sc.doc.Views.Redraw()

    # -------------------------------
    # Finish
    # -------------------------------
    def save_sticky(self):
        sc.sticky[STICKY_PREFIX + "vehicle_idx"] = self.vehicle.SelectedIndex
        sc.sticky[STICKY_PREFIX + "mode_idx"] = self.mode.SelectedIndex
        sc.sticky[STICKY_PREFIX + "road_layout_idx"] = self.road_layout.SelectedIndex
        sc.sticky[STICKY_PREFIX + "steering_model_idx"] = self.steering_model.SelectedIndex
        sc.sticky[STICKY_PREFIX + "radius_m"] = float(self.radius.Value)
        sc.sticky[STICKY_PREFIX + "lock_to_lock_s"] = float(self.lock_to_lock.Value)
        sc.sticky[STICKY_PREFIX + "clearance_m"] = float(self.clearance.Value)
        sc.sticky[STICKY_PREFIX + "road_width_m"] = float(self.road_width.Value)
        sc.sticky[STICKY_PREFIX + "lead_m"] = float(self.lead.Value)
        sc.sticky[STICKY_PREFIX + "spacing_m"] = float(self.spacing.Value)
        outs = self.outputs()
        sc.sticky[STICKY_PREFIX + "out_envelope"] = outs["envelope"]
        sc.sticky[STICKY_PREFIX + "out_clearance"] = outs["clearance_envelope"]
        sc.sticky[STICKY_PREFIX + "out_axles"] = outs["axles"]
        sc.sticky[STICKY_PREFIX + "out_positions"] = outs["positions"]
        sc.sticky[STICKY_PREFIX + "out_road_edges"] = outs["road_edges"]
        sc.sticky[STICKY_PREFIX + "out_road_outline"] = outs["road_outline"]
        sc.sticky[STICKY_PREFIX + "out_reference"] = outs["reference"]

    def on_create(self, sender, e):
        if self.latest_geom is None:
            return
        self.save_sticky()
        self.accepted = True
        self.Close(True)

    def on_cancel(self, sender, e):
        self.accepted = False
        self.Close(False)

    def on_closed(self, sender, e):
        self.conduit.Enabled = False
        sc.doc.Views.Redraw()


# -----------------------------------------------------------------------------
# Command entry point
# -----------------------------------------------------------------------------
def run_vehicle_turn():
    print("VehicleTurn V0.11 - Vej front-axle template + experimental SmartPath")
    print("Select INCOMING then OUTGOING straight 3D centrelines. Pick on the intended travel sides.")

    ref_in, line_in, pick_in = get_linear_curve("Select INCOMING centreline (pick approach side)")
    if line_in is None:
        return
    ref_out, line_out, pick_out = get_linear_curve("Select OUTGOING centreline (pick departure side)")
    if line_out is None:
        return

    dlg = VehicleTurnDialog(line_in, pick_in, line_out, pick_out)
    parent = RhinoEtoApp.MainWindowForDocument(sc.doc)

    try:
        EtoExtensions.ShowSemiModal(dlg, sc.doc, parent)
    finally:
        dlg.conduit.Enabled = False
        sc.doc.Views.Redraw()

    if not dlg.accepted or dlg.latest_geom is None:
        return

    turn = dlg.latest_turn
    vk = dlg.vehicle_key()
    mode = dlg.mode_key()

    # Rebuild once at commit quality. Preview intentionally uses coarser sampling so Eto stays responsive.
    lead = metres_to_model(float(dlg.lead.Value))
    position_spacing = metres_to_model(float(dlg.spacing.Value))
    clearance = metres_to_model(float(dlg.clearance.Value))
    road_width = metres_to_model(float(dlg.road_width.Value))
    fine_step = max(metres_to_model(0.03), sc.doc.ModelAbsoluteTolerance * 6.0)
    poses = sample_front_template(turn, lead, fine_step, vk) if dlg.steering_model_key() == "template" else sample_turn(turn, lead, fine_step)
    geom = build_geometry(
        poses, turn, vk, clearance, road_width, dlg.outputs(), position_spacing,
        dlg.line_in, dlg.line_out, preview=False,
    )
    ids = commit_geometry(geom, dlg.outputs(), vk, mode)

    print("VehicleTurn created {0} ordinary curve(s) on the current layer.".format(len(ids)))
    print("  {0}, Mode {1}, path radius {2:.2f} m ({3})".format(vk, mode, float(dlg.radius.Value), dlg.steering_model_key()))
    print("  Incoming grade {0:+.1f}% | outgoing {1:+.1f}% | max blended {2:.1f}%".format(
        turn["g1"] * 100.0,
        turn["g2"] * 100.0,
        max_profile_grade(poses) * 100.0,
    ))


if __name__ == "__main__":
    run_vehicle_turn()
