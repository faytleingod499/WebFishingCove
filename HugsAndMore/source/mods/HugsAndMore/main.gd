extends Node

# Hugs and More
# Emotes for two players who both have the mod. For now: a hug.
#
# Walk up to another player who has the mod and press the game's interact key at "Hug <name>". They get a
# little invitation ("<name> wants a hug!") and hug back with Y or the Hug button. Then both of you turn to
# each other, step in, put your arms around each other and sway a little, sized to fit both of you
# (a small player hugs a big one around the middle). It ends after a few seconds, or as soon as either
# of you walks away.
#
# Everything goes through the Steam lobby member data ("hugsandmore"), so every player with the mod sees
# the hug. Players without the mod see nothing unusual.

const VERSION = "1.0.0"
const SYNC_KEY = "hugsandmore"   # "1;<seq>;<ask|yes>;<emote>;<target steam id>;<the seq it answers>"
const AREA_SCRIPT = "res://mods/HugsAndMore/emote_area.gd"
const SKELETON_PATH = "body/player_body/Armature/Skeleton"
const POPUP_ID = "HUGSANDMORE"

const ASK_TIME = 15.0      # an invitation is open this long
const HUG_TIME = 5.0       # seconds of hugging
const BLEND_TIME = 0.35    # easing in and out of the pose
const START_RANGE = 2.6    # the two have to be about this close when the hug starts
const MOVE_LIMIT = 0.6     # walking further than this from where the hug started ends it
const ACCEPT_KEY = KEY_Y

var steam = null
var area_script = null

# my own state
var my_seq = 0
var my_action = ""         # "ask" or "yes", "" when idle
var my_emote = ""
var my_target = 0
var my_ref = 0
var my_since = 0.0
var sync_dirty = false
var announced_lobby = 0    # the lobby I've told I have the mod

# everyone's state, read from the lobby: steam id -> {seq, action, emote, target, ref}
var states = {}
var read_timer = 0.0
var clock = 0.0

# hugs going on: "a-b" -> {a, b, seq, t, w, start_a, start_b, ending}
var hugs = {}
var areas = {}             # steam id -> the "Hug" spot on that player
var answered = {}          # "<steam id>-<seq>" of invitations already answered or declined

# the invitation box
var layer
var invite_panel
var invite_label
var invite_from = 0
var invite_seq = 0
var toast
var toast_until = 0.0


func _ready():
	if Engine.has_singleton("Steam"):
		steam = Engine.get_singleton("Steam")
	if ResourceLoader.exists(AREA_SCRIPT):
		area_script = load(AREA_SCRIPT)
	_build_ui()


func _exit_tree():
	for key in hugs.keys():
		_clear_pose(hugs[key])
	_set_state("", "", 0, 0)
	_flush_state()


# --- every frame ---

func _process(delta):
	clock += delta
	var net = get_node_or_null("/root/Network")
	var lobby = 0
	if net != null and not net.PLAYING_OFFLINE and net.STEAM_LOBBY_ID > 0:
		lobby = net.STEAM_LOBBY_ID
	var me = int(net.STEAM_ID) if net != null else 0

	# my invitation or answer runs out
	if my_action != "" and clock - my_since > ASK_TIME and not _in_hug(me):
		_set_state("", "", 0, 0)

	# tell the lobby once that I have the mod, so others get the "Hug" prompt on me even while I'm idle
	if lobby > 0 and lobby != announced_lobby and steam != null and steam.has_method("setLobbyMemberData"):
		announced_lobby = lobby
		steam.setLobbyMemberData(lobby, SYNC_KEY + "_v", VERSION)
		sync_dirty = true

	read_timer += delta
	if read_timer >= 0.2:
		read_timer = 0.0
		_read_states(lobby, me)
		if sync_dirty:
			_flush_state()

	var players = _players()
	_update_areas(players, me)
	_update_invite(me)
	_update_hugs(players, me, delta)
	toast.visible = clock < toast_until


func _players():
	var out = {}
	for player in get_tree().get_nodes_in_group("player"):
		if not is_instance_valid(player) or player.get("dead_actor") == true or player.get("NPC_body") == true:
			continue
		var id = player.get("owner_id")
		if id != null and int(id) > 0:
			out[int(id)] = player
	return out


# --- talking through the lobby ---

func _set_state(action, emote, target, ref):
	my_action = action
	my_emote = emote
	my_target = int(target)
	my_ref = int(ref)
	my_since = clock
	if action != "":
		my_seq += 1
	sync_dirty = true


func _state_text():
	if my_action == "":
		return ""
	return "1;" + str(my_seq) + ";" + my_action + ";" + my_emote + ";" + str(my_target) + ";" + str(my_ref)


func _flush_state():
	sync_dirty = false
	var net = get_node_or_null("/root/Network")
	if steam == null or net == null or net.PLAYING_OFFLINE or net.STEAM_LOBBY_ID <= 0:
		return
	if steam.has_method("setLobbyMemberData"):
		steam.setLobbyMemberData(net.STEAM_LOBBY_ID, SYNC_KEY, _state_text())


func _parse(text):
	var bits = str(text).split(";")
	if bits.size() < 6 or bits[0] != "1":
		return null
	return {"seq": int(bits[1]), "action": bits[2], "emote": bits[3], "target": int(bits[4]), "ref": int(bits[5])}


func _read_states(lobby, me):
	states = {}
	if me > 0:
		states[me] = {"seq": my_seq, "action": my_action, "emote": my_emote, "target": my_target, "ref": my_ref, "mod": true}
	if lobby <= 0 or steam == null or not steam.has_method("getLobbyMemberData"):
		return
	for player in get_tree().get_nodes_in_group("player"):
		var id = player.get("owner_id")
		if id == null or int(id) <= 0 or int(id) == me:
			continue
		var text = str(steam.getLobbyMemberData(lobby, int(id), SYNC_KEY))
		var state = _parse(text)
		if state == null:
			state = {"seq": 0, "action": "", "emote": "", "target": 0, "ref": 0}
		# players with the mod announce it under "hugsandmore_v" (their state is empty while idle)
		state["mod"] = text != "" or str(steam.getLobbyMemberData(lobby, int(id), SYNC_KEY + "_v")) != ""
		states[int(id)] = state


# --- the "Hug <name>" spot on other players ---

func _update_areas(players, me):
	for id in areas.keys():
		if not players.has(id) or not is_instance_valid(areas[id]):
			if is_instance_valid(areas[id]):
				areas[id].queue_free()
			areas.erase(id)
	if area_script == null:
		return
	for id in players.keys():
		if id == me or not states.has(id) or not states[id].get("mod", false):
			continue
		if not areas.has(id):
			var area = Area.new()
			area.set_script(area_script)
			area.mod = self
			area.target_id = id
			var shape = CollisionShape.new()
			var capsule = SphereShape.new()
			capsule.radius = 0.7
			shape.shape = capsule
			area.add_child(shape)
			players[id].add_child(area)
			areas[id] = area
		var area = areas[id]
		area.text = "Hug " + _name_of(id)
		# not while either of you is already hugging, or while you're already asking them
		area.visible = not _in_hug(me) and not _in_hug(id) and not (my_action == "ask" and my_target == id)


func _on_area_activated(target_id, actor):
	if actor == null or actor.get("controlled") != true:
		return
	var net = get_node_or_null("/root/Network")
	if net == null or _in_hug(int(net.STEAM_ID)):
		return
	# they asked me first: that's a yes
	var theirs = states.get(int(target_id))
	if theirs != null and theirs["action"] == "ask" and theirs["target"] == int(net.STEAM_ID):
		_accept(int(target_id), theirs["seq"])
		return
	_set_state("ask", "hug", target_id, 0)
	_flush_state()
	_show_toast("Asked " + _name_of(target_id) + " for a hug...")


# --- an invitation for me ---

func _update_invite(me):
	var from = 0
	var seq = 0
	for id in states.keys():
		var s = states[id]
		if id != me and s["action"] == "ask" and s["emote"] == "hug" and s["target"] == me \
				and not answered.has(str(id) + "-" + str(s["seq"])) and not _in_hug(me) and not _in_hug(id):
			from = id
			seq = s["seq"]
			break
	if from != invite_from or seq != invite_seq:
		invite_from = from
		invite_seq = seq
		if from != 0:
			invite_label.text = _name_of(from) + " wants a hug!"
	invite_panel.visible = invite_from != 0 and _hud_free()
	if invite_panel.visible:
		var size = invite_panel.get_viewport_rect().size
		invite_panel.rect_size = Vector2()
		invite_panel.rect_position = Vector2((size.x - invite_panel.rect_size.x) / 2.0, size.y * 0.18)


func _input(event):
	if invite_from == 0 or not invite_panel.visible:
		return
	if event is InputEventKey and event.pressed and not event.echo and event.scancode == ACCEPT_KEY:
		var hud = _get_hud()
		if hud != null and hud.get("using_chat") == true:
			return
		_on_invite_yes()
		get_tree().set_input_as_handled()


func _on_invite_yes():
	if invite_from != 0:
		_accept(invite_from, invite_seq)


func _on_invite_no():
	if invite_from != 0:
		answered[str(invite_from) + "-" + str(invite_seq)] = true
		invite_from = 0
		invite_panel.visible = false


func _accept(from, seq):
	answered[str(from) + "-" + str(seq)] = true
	_set_state("yes", "hug", from, seq)
	_flush_state()
	invite_from = 0
	invite_panel.visible = false


# --- hugs ---

func _in_hug(id):
	for key in hugs.keys():
		var h = hugs[key]
		if not h["ending"] and (h["a"] == id or h["b"] == id):
			return true
	return false


func _update_hugs(players, me, delta):
	# a hug is on while one asked and the other said yes to that very invitation (or both asked each other)
	var wanted = {}
	for a in states.keys():
		var s = states[a]
		if s["action"] != "ask" or s["emote"] != "hug" or not states.has(s["target"]):
			continue
		var b = s["target"]
		var t = states[b]
		var yes = t["action"] == "yes" and t["target"] == a and t["ref"] == s["seq"]
		var both_asked = t["action"] == "ask" and t["target"] == a
		if yes or (both_asked and a < b):
			wanted[str(a) + "-" + str(s["seq"])] = [a, b]

	for key in wanted.keys():
		if hugs.has(key):
			continue
		var a = wanted[key][0]
		var b = wanted[key][1]
		if not players.has(a) or not players.has(b) or _busy(a) or _busy(b):
			continue
		var pa = players[a].global_transform.origin
		var pb = players[b].global_transform.origin
		if Vector2(pa.x - pb.x, pa.z - pb.z).length() > START_RANGE:
			if a == me or b == me:
				_show_toast("Too far apart for a hug")
				_set_state("", "", 0, 0)
			continue
		hugs[key] = {"a": a, "b": b, "t": 0.0, "w": 0.0, "start_a": pa, "start_b": pb, "ending": false,
			"pa": players[a], "pb": players[b]}

	for key in hugs.keys():
		var h = hugs[key]
		h["t"] += delta
		var gone = not is_instance_valid(h["pa"]) or not is_instance_valid(h["pb"])
		if not gone and not h["ending"]:
			var moved = _flat(h["pa"].global_transform.origin - h["start_a"]).length() > MOVE_LIMIT \
				or _flat(h["pb"].global_transform.origin - h["start_b"]).length() > MOVE_LIMIT
			if moved or h["t"] > HUG_TIME or not wanted.has(key) and h["t"] > 1.0:
				h["ending"] = true
				if h["a"] == me or h["b"] == me:
					_set_state("", "", 0, 0)
		if gone:
			_clear_pose(h)
			hugs.erase(key)
			continue
		h["w"] = clamp(h["w"] + (delta if not h["ending"] else -delta) / BLEND_TIME, 0.0, 1.0)
		if h["ending"] and h["w"] <= 0.0:
			_clear_pose(h)
			hugs.erase(key)
			continue
		_pose_hug(h)


func _busy(id):
	for key in hugs.keys():
		if hugs[key]["a"] == id or hugs[key]["b"] == id:
			return true
	return false


func _flat(v):
	return Vector3(v.x, 0, v.z)


# --- the pose ---

# Both players turn to face each other and step in to meet in the middle, then wrap their arms around
# the other: the hands reach behind the other's back at a height that suits both (a small player
# hugs a big one around the middle, a big one hugs a small one around the shoulders). The two sway
# gently together. All of it is drawn with bone overrides, nobody's real position changes.
func _pose_hug(h):
	var pa = h["pa"]
	var pb = h["pb"]
	var sa = _player_size(pa)
	var sb = _player_size(pb)
	var A = _flat(pa.global_transform.origin)
	var B = _flat(pb.global_transform.origin)
	var dir = B - A
	if dir.length() < 0.01:
		dir = _facing(pa)
	dir = dir.normalized()
	var mid = (A + B) / 2.0
	var gap = 0.30 * (sa + sb) / 2.0           # chest to chest
	var sway = deg2rad(4.0) * sin(h["t"] * 2.2) * h["w"]
	_pose_one(pa, pb, dir, mid, gap, sa, sb, sway, h["w"], 1.0)
	_pose_one(pb, pa, -dir, mid, gap, sb, sa, sway, h["w"], -1.0)


func _pose_one(me_node, other, dir, mid, gap, s_me, s_other, sway, w, tilt_side):
	var sk = me_node.get_node_or_null(SKELETON_PATH)
	var osk = other.get_node_or_null(SKELETON_PATH)
	if sk == null or osk == null:
		return
	var pelvis = sk.find_bone("pelvis")
	var head = sk.find_bone("head")
	if pelvis < 0:
		return
	var W = sk.global_transform
	var Winv = W.affine_inverse()
	var here = me_node.global_transform.origin

	# where my body goes and which way it faces: turned to the other, stepped in to the middle, swaying
	var fwd = _facing(me_node)
	var turn = atan2(fwd.cross(dir).y, fwd.dot(dir))
	var spot = mid - dir * gap / 2.0
	var move = Transform(Basis(), Vector3(spot.x - here.x, 0, spot.z - here.z))
	var spin = Transform(Basis(), here) * Transform(Basis(Vector3.UP, turn), Vector3()) * Transform(Basis(), -here)
	var swaying = Transform(Basis(), mid) * Transform(Basis(Vector3.UP, sway), Vector3()) * Transform(Basis(), -mid)
	var world_move = swaying * move * spin
	var rebase = Winv * world_move * W         # moves any of my bones (skeleton space) to where they go now

	var g_pelvis = sk.get_bone_global_pose_no_override(pelvis)
	sk.set_bone_global_pose_override(pelvis, rebase * g_pelvis, w, true)

	# the other's body, where it stands in the hug: their middle, and the height my arms go round
	var their_spot = mid + dir * gap / 2.0
	var my_shoulder_y = 0.0
	var their_shoulder_y = 0.0
	var arm_l = sk.find_bone("arm.l")
	var arm_r = sk.find_bone("arm.r")
	if arm_l >= 0:
		my_shoulder_y = W.xform(sk.get_bone_global_pose_no_override(arm_l).origin).y
	var o_arm = osk.find_bone("arm.l")
	if o_arm >= 0:
		their_shoulder_y = osk.global_transform.xform(osk.get_bone_global_pose_no_override(o_arm).origin).y
	var their_feet = other.global_transform.origin.y - 1.0 * s_other
	var height = min(my_shoulder_y - 0.12 * s_me, their_shoulder_y - 0.06 * s_other)
	height = max(height, their_feet + 0.35 * s_other)
	var radius = 0.24 * s_other                # roughly how round the other's body is

	var left = Vector3.UP.cross(dir).normalized()
	for side in [["arm.l", "elbow.l", "hand.l"], ["arm.r", "elbow.r", "hand.r"]]:
		var arm = sk.find_bone(side[0])
		var elbow = sk.find_bone(side[1])
		var hand = sk.find_bone(side[2])
		if arm < 0 or elbow < 0 or hand < 0:
			continue
		var g_arm = sk.get_bone_global_pose_no_override(arm)
		var g_elbow = sk.get_bone_global_pose_no_override(elbow)
		var g_hand = sk.get_bone_global_pose_no_override(hand)
		var a = rebase * g_arm
		var shoulder_w = W.xform(a.origin)
		var s = sign(_flat(shoulder_w - spot).dot(left))
		if s == 0:
			s = 1.0
		# behind their back, a little to my side of their middle
		var target_w = Vector3(their_spot.x, height, their_spot.z) + dir * radius * 0.4 + left * s * radius * 0.6
		var pole_w = left * s * 1.0 + Vector3.DOWN * 0.6 - dir * 0.2
		_two_bone(sk, arm, elbow, hand, a, rebase * g_elbow, rebase * g_hand, Winv.xform(target_w),
			Winv.basis.xform(pole_w).normalized(), g_arm, g_elbow, w)

	# heads tip to opposite sides, so they rest past each other
	if head >= 0:
		var g_head = rebase * sk.get_bone_global_pose_no_override(head)
		var axis = Winv.basis.xform(dir).normalized()
		sk.set_bone_global_pose_override(head, Transform(Basis(axis, deg2rad(12.0) * tilt_side) * g_head.basis, g_head.origin), w, true)


# a two-bone reach: the elbow bends out to the side and down, the fingertips end at the target
func _two_bone(sk, arm, elbow, hand, a, e, hnd, target, pole, g_arm, g_elbow, w):
	var finger = 0.08
	var S = a.origin
	var E0 = e.origin
	var H0 = hnd.origin + (hnd.origin - e.origin).normalized() * finger
	var la = S.distance_to(E0)
	var lb = E0.distance_to(H0)
	var to = target - S
	var d = clamp(to.length(), abs(la - lb) + 0.001, la + lb - 0.001)
	var u = to.normalized()
	var cos_a = clamp((la * la + d * d - lb * lb) / (2.0 * la * d), -1.0, 1.0)
	var p = pole - u * pole.dot(u)
	if p.length() < 0.001:
		p = Vector3.DOWN - u * Vector3.DOWN.dot(u)
	p = p.normalized()
	var E = S + u * la * cos_a + p * la * sqrt(max(0.0, 1.0 - cos_a * cos_a))
	var T = S + u * d

	var arm_new = Transform(_turn(E0 - S, E - S) * a.basis, S)
	var elbow_mid = arm_new * (g_arm.affine_inverse() * g_elbow)
	var tip_local = g_elbow.affine_inverse().xform(sk.get_bone_global_pose_no_override(hand).origin)
	var tip_dir = (sk.get_bone_global_pose_no_override(hand).origin - g_elbow.origin).normalized()
	var hand_now = elbow_mid.xform(tip_local) + elbow_mid.basis.xform(g_elbow.basis.inverse().xform(tip_dir)) * finger
	var elbow_new = Transform(_turn(hand_now - elbow_mid.origin, T - elbow_mid.origin) * elbow_mid.basis, elbow_mid.origin)
	sk.set_bone_global_pose_override(arm, arm_new, w, true)
	sk.set_bone_global_pose_override(elbow, elbow_new, w, true)


# the rotation that turns direction v onto direction d
func _turn(v, d):
	v = v.normalized()
	d = d.normalized()
	var c = v.cross(d)
	if c.length() < 0.00001:
		return Basis()
	return Basis(c.normalized(), acos(clamp(v.dot(d), -1.0, 1.0)))


func _clear_pose(h):
	for node in [h.get("pa"), h.get("pb")]:
		if node == null or not is_instance_valid(node):
			continue
		var sk = node.get_node_or_null(SKELETON_PATH)
		if sk == null:
			continue
		for name in ["pelvis", "head", "arm.l", "elbow.l", "arm.r", "elbow.r"]:
			var bone = sk.find_bone(name)
			if bone >= 0:
				sk.set_bone_global_pose_override(bone, Transform(), 0.0, true)


# --- helpers ---

func _player_size(player):
	var sk = player.get_node_or_null(SKELETON_PATH)
	if sk != null:
		return sk.global_transform.basis.get_scale().y
	return player.global_transform.basis.get_scale().y


func _facing(node):
	var f = -node.global_transform.basis.z
	f.y = 0
	return f.normalized() if f.length() > 0.001 else Vector3(0, 0, -1)


func _name_of(id):
	var net = get_node_or_null("/root/Network")
	if net != null:
		for member in net.get("LOBBY_MEMBERS") if net.get("LOBBY_MEMBERS") != null else []:
			if int(member.get("steam_id", 0)) == int(id):
				return str(member.get("steam_name", "someone"))
	if steam != null and steam.has_method("getFriendPersonaName"):
		return str(steam.getFriendPersonaName(int(id)))
	return "someone"


func _get_hud():
	var players = get_tree().get_nodes_in_group("controlled_player")
	if players.empty():
		return null
	var hud = players[0].get("hud")
	if hud == null or not is_instance_valid(hud):
		return null
	return hud


func _hud_free():
	var hud = _get_hud()
	if hud == null:
		return false
	return hud.get("menu") == 0 and hud.get("hud_hidden") != true


# --- the invitation box ---

func _build_ui():
	layer = CanvasLayer.new()
	layer.layer = 3
	add_child(layer)

	invite_panel = PanelContainer.new()
	invite_panel.visible = false
	invite_panel.add_stylebox_override("panel", _glass(Color(0.06, 0.12, 0.18, 0.75), Color(1.0, 0.7, 0.85, 0.7), 12, 6))
	layer.add_child(invite_panel)
	var margin = MarginContainer.new()
	for side in ["left", "right"]:
		margin.add_constant_override("margin_" + side, 14)
	for side in ["top", "bottom"]:
		margin.add_constant_override("margin_" + side, 8)
	invite_panel.add_child(margin)
	var row = HBoxContainer.new()
	row.add_constant_override("separation", 10)
	margin.add_child(row)
	invite_label = Label.new()
	invite_label.add_color_override("font_color", Color(1, 1, 1))
	invite_label.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	row.add_child(invite_label)
	var yes = Button.new()
	yes.text = "Hug (Y)"
	yes.focus_mode = Control.FOCUS_NONE
	yes.connect("pressed", self, "_on_invite_yes")
	yes.add_stylebox_override("normal", _glass(Color(0.85, 0.35, 0.55, 0.55), Color(1.0, 0.75, 0.85, 0.8), 8, 0))
	yes.add_stylebox_override("hover", _glass(Color(0.95, 0.45, 0.65, 0.7), Color(1.0, 0.85, 0.9, 1.0), 8, 0))
	yes.add_stylebox_override("pressed", _glass(Color(0.7, 0.25, 0.45, 0.7), Color(1.0, 0.85, 0.9, 1.0), 8, 0))
	row.add_child(yes)
	var no = Button.new()
	no.text = "No thanks"
	no.focus_mode = Control.FOCUS_NONE
	no.connect("pressed", self, "_on_invite_no")
	no.add_stylebox_override("normal", _glass(Color(0, 0, 0, 0.3), Color(0.55, 1.0, 1.0, 0.35), 8, 0))
	no.add_stylebox_override("hover", _glass(Color(0, 0, 0, 0.4), Color(0.8, 1.0, 1.0, 0.8), 8, 0))
	no.add_stylebox_override("pressed", _glass(Color(0, 0, 0, 0.5), Color(0.8, 1.0, 1.0, 0.8), 8, 0))
	row.add_child(no)

	toast = Label.new()
	toast.visible = false
	toast.add_color_override("font_color", Color(1, 1, 1))
	toast.add_color_override("font_color_shadow", Color(0, 0, 0, 0.8))
	toast.add_constant_override("shadow_offset_x", 1)
	toast.add_constant_override("shadow_offset_y", 1)
	toast.anchor_left = 0.5
	toast.anchor_right = 0.5
	toast.anchor_top = 0.24
	toast.anchor_bottom = 0.24
	toast.grow_horizontal = Control.GROW_DIRECTION_BOTH
	toast.align = Label.ALIGN_CENTER
	layer.add_child(toast)


func _show_toast(text):
	toast.text = text
	toast_until = clock + 3.0


func _glass(fill, edge, radius, shadow):
	var box = StyleBoxFlat.new()
	box.bg_color = fill
	box.border_color = edge
	box.set_border_width_all(1)
	box.set_corner_radius_all(radius)
	box.content_margin_left = 8
	box.content_margin_right = 8
	box.content_margin_top = 3
	box.content_margin_bottom = 3
	box.shadow_color = Color(0, 0, 0, 0.25)
	box.shadow_size = shadow
	return box
