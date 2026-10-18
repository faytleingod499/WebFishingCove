extends Node

# Hugs and More
# Emotes for two players who both have the mod: a hug, a high five and a kiss (more to come).
#
# Walk up to another player who has the mod: a small prompt ("F  Interact") floats above them. Press F and
# a little window opens with the emotes to pick from. They get an invitation ("<name> wants a hug!") and
# answer with Y or the button. Then both of you play it together, sized to fit both of you:
# - Hug: you turn to each other, step in, put your arms around each other and sway a little. It ends
#   after a few seconds, or as soon as either of you walks away.
# - High five: you face each other, raise your hands and slap them together above your heads.
# - Kiss: you lean in to each other, pucker up, the kiss sound plays and hearts float up.
# Hugs get little floating hearts too.
#
# Everything goes through the Steam lobby member data ("hugsandmore"), so every player with the mod sees
# it. Players without the mod see nothing unusual.

const VERSION = "1.2.3"
const SYNC_KEY = "hugsandmore"   # "1;<seq>;<ask|yes>;<emote>;<target steam id>;<the seq it answers>"
const SKELETON_PATH = "body/player_body/Armature/Skeleton"
const POPUP_ID = "HUGSANDMORE"
const SLAP_SOUND = "res://Sounds/punch.ogg"
const KISS_SOUND = "res://Sounds/kiss.ogg"
const HEARTS = "res://Scenes/Particles/kiss.tscn"   # the game's own little hearts

# the emotes: what the window shows, what the invitation says, the answer button, how long it lasts
const EMOTE_ORDER = ["hug", "highfive", "kiss"]
const EMOTES = {
	"hug": {"label": "Hug", "ask": "wants a hug!", "answer": "Hug back", "time": 5.0},
	"highfive": {"label": "High five", "ask": "wants a high five!", "answer": "High five", "time": 1.5},
	"kiss": {"label": "Kiss", "ask": "wants a kiss!", "answer": "Kiss", "time": 2.2},
}

const ASK_TIME = 15.0      # an invitation is open this long
const BLEND_TIME = 0.3     # easing in and out of a pose
const START_RANGE = 2.6    # the two have to be about this close when it starts
const MOVE_LIMIT = 0.6     # walking further than this from where it started ends it
const PROMPT_RANGE = 2.0   # how close you have to be for the prompt
const OPEN_KEY = KEY_F
const ACCEPT_KEY = KEY_Y

var steam = null
var slap_sound = null
var kiss_sound = null
var hearts_scene = null

# my own state
var my_seq = 0
var my_action = ""         # "ask" or "yes", "" when idle
var my_emote = ""
var my_target = 0
var my_ref = 0
var my_since = 0.0
var sync_dirty = false
var announced_lobby = 0    # the lobby I've told I have the mod
var next_announce = 0.0
var seen_mod = {}          # players already reported in the log as having the mod

# everyone's state, read from the lobby: steam id -> {seq, action, emote, target, ref, mod}
var states = {}
var read_timer = 0.0
var clock = 0.0

# emotes going on: "<asker>-<seq>" -> {emote, a, b, t, w, start_a, start_b, ending, pa, pb, slapped}
var pairs = {}
var answered = {}          # "<steam id>-<seq>" of invitations already answered or declined

# the prompt over the nearest player, the window to pick an emote, the invitation box
var layer
var prompt_layer
var prompt
var prompt_target = 0
var picker
var picker_title
var picker_target = 0
var locked_hud = null
var invite_panel
var invite_label
var invite_yes
var invite_from = 0
var invite_seq = 0
var invite_emote = ""
var toast
var toast_until = 0.0


func _ready():
	if Engine.has_singleton("Steam"):
		steam = Engine.get_singleton("Steam")
	if ResourceLoader.exists(SLAP_SOUND):
		slap_sound = load(SLAP_SOUND)
	if ResourceLoader.exists(KISS_SOUND):
		kiss_sound = load(KISS_SOUND)
	if ResourceLoader.exists(HEARTS):
		hearts_scene = load(HEARTS)
	_build_ui()


func _exit_tree():
	_close_picker()
	for key in pairs.keys():
		_clear_pose(pairs[key])
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
	if my_action != "" and clock - my_since > ASK_TIME and not _busy(me):
		_set_state("", "", 0, 0)

	# tell the lobby that I have the mod, so others get the prompt on me even while I'm idle. Checked again
	# every few seconds: a write made while still joining the lobby is lost, so it is redone until it sticks.
	if lobby > 0 and steam != null and steam.has_method("setLobbyMemberData") and clock >= next_announce:
		next_announce = clock + 3.0
		var mine = str(steam.getLobbyMemberData(lobby, me, SYNC_KEY + "_v")) if steam.has_method("getLobbyMemberData") else ""
		if mine != VERSION:
			steam.setLobbyMemberData(lobby, SYNC_KEY + "_v", VERSION)
			if lobby != announced_lobby:
				print("[HugsAndMore] ", VERSION, " announced in lobby ", lobby)
			announced_lobby = lobby
			sync_dirty = true
		elif str(steam.getLobbyMemberData(lobby, me, SYNC_KEY)) != _state_text():
			sync_dirty = true # my state didn't stick either

	read_timer += delta
	if read_timer >= 0.2:
		read_timer = 0.0
		_read_states(lobby, me)
		if sync_dirty:
			_flush_state()

	var players = _players()
	_update_prompt(players, me)
	_update_picker(players, me)
	_update_invite(me)
	_update_pairs(players, me, delta)
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
		if state["mod"] and not seen_mod.has(int(id)):
			seen_mod[int(id)] = true
			print("[HugsAndMore] ", _name_of(int(id)), " has the mod")


# --- the small prompt over the nearest player with the mod ---

func _update_prompt(players, me):
	prompt_target = 0
	if players.has(me) and _hud_free() and not _busy(me) and picker_target == 0:
		var here = players[me].global_transform.origin
		var best = PROMPT_RANGE
		for id in players.keys():
			if id == me or not states.has(id) or not states[id].get("mod", false) or _busy(id):
				continue
			var d = _flat(players[id].global_transform.origin - here).length()
			if d < best:
				best = d
				prompt_target = id
	if prompt_target == 0:
		prompt.visible = false
		return
	_place_over(prompt, players[prompt_target], 0.0)


# puts a small box on screen just above a player's head
func _place_over(control, player, lift):
	var view = player.get_viewport()
	var camera = view.get_camera() if view != null else null
	if camera == null or not player.is_visible_in_tree():
		control.visible = false
		return
	var top = player.global_transform.origin + Vector3.UP * (1.25 + lift) * _player_size(player)
	var sk = player.get_node_or_null(SKELETON_PATH)
	if sk != null and sk.find_bone("head") >= 0:
		top = sk.global_transform.xform(sk.get_bone_global_pose(sk.find_bone("head")).origin) + Vector3.UP * (0.75 + lift) * _player_size(player)
	if camera.is_position_behind(top):
		control.visible = false
		return
	var at = camera.unproject_position(top)
	var holder = view.get_parent()
	if holder is ViewportContainer and view.size.x > 0 and view.size.y > 0:
		var box = holder.get_global_rect()
		at = box.position + at * (box.size / view.size)
	control.visible = true
	control.rect_size = Vector2()
	var size = control.get_combined_minimum_size()
	control.rect_position = Vector2(at.x - size.x / 2.0, at.y - size.y)


func _input(event):
	if not (event is InputEventKey) or not event.pressed or event.echo:
		return
	var hud = _get_hud()
	if hud != null and hud.get("using_chat") == true:
		return
	if event.scancode == ACCEPT_KEY and invite_from != 0 and invite_panel.visible:
		_on_invite_yes()
		get_tree().set_input_as_handled()
	elif event.scancode == OPEN_KEY:
		if picker_target != 0:
			_close_picker()
			get_tree().set_input_as_handled()
		elif prompt_target != 0 and prompt.visible:
			_open_picker(prompt_target)
			get_tree().set_input_as_handled()
	elif event.scancode == KEY_ESCAPE and picker_target != 0:
		_close_picker()
		get_tree().set_input_as_handled()


# --- the window to pick an emote ---

func _open_picker(target):
	picker_target = target
	picker_title.text = _name_of(target)
	picker.visible = true
	prompt.visible = false
	_lock_hud()


func _close_picker():
	picker_target = 0
	if picker != null:
		picker.visible = false
	_unlock_hud()


func _update_picker(players, me):
	if picker_target == 0:
		return
	# it closes when they walk off, a menu opens, or something is already going on
	if not players.has(picker_target) or not players.has(me) or _busy(me) or _busy(picker_target) \
			or _flat(players[picker_target].global_transform.origin - players[me].global_transform.origin).length() > START_RANGE + 1.0 \
			or not _hud_free(true):
		_close_picker()
		return
	_place_over(picker, players[picker_target], 0.15)


func _on_pick(kind):
	var target = picker_target
	_close_picker()
	if target == 0:
		return
	# they asked me for the same thing first: that's a yes
	var theirs = states.get(int(target))
	var net = get_node_or_null("/root/Network")
	var me = int(net.STEAM_ID) if net != null else 0
	if theirs != null and theirs["action"] == "ask" and theirs["emote"] == kind and theirs["target"] == me:
		_accept(int(target), theirs["seq"], kind)
		return
	_set_state("ask", kind, target, 0)
	_flush_state()
	_show_toast("Asked " + _name_of(target) + " for a " + EMOTES[kind]["label"].to_lower() + "...")


# --- an invitation for me ---

func _update_invite(me):
	var from = 0
	var seq = 0
	var kind = ""
	for id in states.keys():
		var s = states[id]
		if id != me and s["action"] == "ask" and EMOTES.has(s["emote"]) and s["target"] == me \
				and not answered.has(str(id) + "-" + str(s["seq"])) and not _busy(me) and not _busy(id):
			from = id
			seq = s["seq"]
			kind = s["emote"]
			break
	if from != invite_from or seq != invite_seq:
		invite_from = from
		invite_seq = seq
		invite_emote = kind
		if from != 0:
			invite_label.text = _name_of(from) + " " + EMOTES[kind]["ask"]
			invite_yes.text = EMOTES[kind]["answer"] + " (Y)"
	invite_panel.visible = invite_from != 0 and _hud_free()
	if invite_panel.visible:
		var size = invite_panel.get_viewport_rect().size
		invite_panel.rect_size = Vector2()
		invite_panel.rect_position = Vector2((size.x - invite_panel.rect_size.x) / 2.0, size.y * 0.18)


func _on_invite_yes():
	if invite_from != 0:
		_accept(invite_from, invite_seq, invite_emote)


func _on_invite_no():
	if invite_from != 0:
		answered[str(invite_from) + "-" + str(invite_seq)] = true
		invite_from = 0
		invite_panel.visible = false


func _accept(from, seq, kind):
	answered[str(from) + "-" + str(seq)] = true
	_set_state("yes", kind, from, seq)
	_flush_state()
	invite_from = 0
	invite_panel.visible = false


# --- emotes going on ---

func _busy(id):
	for key in pairs.keys():
		var p = pairs[key]
		if not p["ending"] and (p["a"] == id or p["b"] == id):
			return true
	return false


func _update_pairs(players, me, delta):
	# one is on while one asked and the other said yes to that very invitation (or both asked each other)
	var wanted = {}
	for a in states.keys():
		var s = states[a]
		if s["action"] != "ask" or not EMOTES.has(s["emote"]) or not states.has(s["target"]):
			continue
		var b = s["target"]
		var t = states[b]
		var yes = t["action"] == "yes" and t["target"] == a and t["ref"] == s["seq"] and t["emote"] == s["emote"]
		var both_asked = t["action"] == "ask" and t["target"] == a and t["emote"] == s["emote"]
		if yes or (both_asked and a < b):
			wanted[str(a) + "-" + str(s["seq"])] = [a, b, s["emote"]]

	for key in wanted.keys():
		if pairs.has(key):
			continue
		var a = wanted[key][0]
		var b = wanted[key][1]
		if not players.has(a) or not players.has(b) or _taken(a) or _taken(b):
			continue
		var pa = players[a].global_transform.origin
		var pb = players[b].global_transform.origin
		if _flat(pa - pb).length() > START_RANGE:
			if a == me or b == me:
				_show_toast("Too far apart")
				_set_state("", "", 0, 0)
			continue
		pairs[key] = {"emote": wanted[key][2], "a": a, "b": b, "t": 0.0, "w": 0.0, "start_a": pa, "start_b": pb,
			"ending": false, "pa": players[a], "pb": players[b], "slapped": false, "next_hearts": 0.3}

	for key in pairs.keys():
		var p = pairs[key]
		p["t"] += delta
		var gone = not is_instance_valid(p["pa"]) or not is_instance_valid(p["pb"])
		if not gone and not p["ending"]:
			var moved = _flat(p["pa"].global_transform.origin - p["start_a"]).length() > MOVE_LIMIT \
				or _flat(p["pb"].global_transform.origin - p["start_b"]).length() > MOVE_LIMIT
			if moved or p["t"] > EMOTES[p["emote"]]["time"] or not wanted.has(key) and p["t"] > 1.0:
				p["ending"] = true
				if p["a"] == me or p["b"] == me:
					_set_state("", "", 0, 0)
		if gone:
			_clear_pose(p)
			pairs.erase(key)
			continue
		p["w"] = clamp(p["w"] + (delta if not p["ending"] else -delta) / BLEND_TIME, 0.0, 1.0)
		if p["ending"] and p["w"] <= 0.0:
			_clear_pose(p)
			pairs.erase(key)
			continue
		call("_pose_" + p["emote"], p)


# anyone in a pair at all, even one easing out
func _taken(id):
	for key in pairs.keys():
		if pairs[key]["a"] == id or pairs[key]["b"] == id:
			return true
	return false


func _flat(v):
	return Vector3(v.x, 0, v.z)


# --- the poses ---
# Everything is drawn with bone overrides on both players, nobody's real position changes. Each body
# (the pelvis) is turned to face the other and stepped to its place; arms reach with a two-bone reach.

# turns and steps one body; returns what the arms need to know
func _place_body(me_node, dir, mid, gap, sway, w):
	var sk = me_node.get_node_or_null(SKELETON_PATH)
	if sk == null:
		return null
	var pelvis = sk.find_bone("pelvis")
	if pelvis < 0:
		return null
	var W = sk.global_transform
	var here = me_node.global_transform.origin
	var fwd = _facing(me_node)
	var turn = atan2(fwd.cross(dir).y, fwd.dot(dir))
	var spot = mid - dir * gap / 2.0
	var move = Transform(Basis(), Vector3(spot.x - here.x, 0, spot.z - here.z))
	var spin = Transform(Basis(), here) * Transform(Basis(Vector3.UP, turn), Vector3()) * Transform(Basis(), -here)
	var swaying = Transform(Basis(), mid) * Transform(Basis(Vector3.UP, sway), Vector3()) * Transform(Basis(), -mid)
	var rebase = W.affine_inverse() * swaying * move * spin * W   # my bones (skeleton space) to where they go now
	sk.set_bone_global_pose_override(pelvis, rebase * sk.get_bone_global_pose_no_override(pelvis), w, true)
	return {"sk": sk, "W": W, "Winv": W.affine_inverse(), "rebase": rebase, "spot": spot}


# reaches one arm (the one on that side of the body) so its fingertips end at a point
func _reach(body, side_sign, target_w, pole_w, left, w):
	var sk = body["sk"]
	for side in [["arm.l", "elbow.l", "hand.l"], ["arm.r", "elbow.r", "hand.r"]]:
		var arm = sk.find_bone(side[0])
		var elbow = sk.find_bone(side[1])
		var hand = sk.find_bone(side[2])
		if arm < 0 or elbow < 0 or hand < 0:
			continue
		var g_arm = sk.get_bone_global_pose_no_override(arm)
		var g_elbow = sk.get_bone_global_pose_no_override(elbow)
		var a = body["rebase"] * g_arm
		var s = sign(_flat(body["W"].xform(a.origin) - body["spot"]).dot(left))
		if s == 0:
			s = 1.0
		if side_sign != 0 and s != side_sign:
			continue
		var t = target_w
		var pole = pole_w
		if typeof(target_w) == TYPE_ARRAY:   # [centre, sideways]: each arm goes to its own side
			t = target_w[0] + target_w[1] * s
			pole = pole_w[0] + pole_w[1] * s
		_two_bone(sk, arm, elbow, hand, a, body["rebase"] * g_elbow, body["rebase"] * sk.get_bone_global_pose_no_override(hand),
			body["Winv"].xform(t), body["Winv"].basis.xform(pole).normalized(), g_arm, g_elbow, w)


func _shoulder_y(player):
	var sk = player.get_node_or_null(SKELETON_PATH)
	if sk == null or sk.find_bone("arm.l") < 0:
		return player.global_transform.origin.y + 0.5 * _player_size(player)
	return sk.global_transform.xform(sk.get_bone_global_pose_no_override(sk.find_bone("arm.l")).origin).y


# tips the head sideways about the facing direction, towards the given side
func _tilt_head_toward(body, axis_w, side, deg, w):
	var sk = body["sk"]
	var head = sk.find_bone("head")
	if head < 0:
		return
	var g = body["rebase"] * sk.get_bone_global_pose_no_override(head)
	var axis = body["Winv"].basis.xform(axis_w).normalized()
	var up = g.basis.xform(Vector3.UP)
	var side_s = body["Winv"].basis.xform(side).normalized()
	if (up.rotated(axis, deg2rad(deg)) - up).dot(side_s) < 0:
		deg = -deg
	sk.set_bone_global_pose_override(head, Transform(Basis(axis, deg2rad(deg)) * g.basis, g.origin), w, true)


func _tilt_head(body, axis_w, deg, w):
	var sk = body["sk"]
	var head = sk.find_bone("head")
	if head < 0:
		return
	var g = body["rebase"] * sk.get_bone_global_pose_no_override(head)
	var axis = body["Winv"].basis.xform(axis_w).normalized()
	sk.set_bone_global_pose_override(head, Transform(Basis(axis, deg2rad(deg)) * g.basis, g.origin), w, true)


func _pair_frame(p):
	var A = _flat(p["pa"].global_transform.origin)
	var B = _flat(p["pb"].global_transform.origin)
	var dir = B - A
	if dir.length() < 0.01:
		dir = _facing(p["pa"])
	return [dir.normalized(), (A + B) / 2.0]


# A hug: both step in to meet in the middle and wrap their arms around the other, the hands behind the
# other's back at a height that suits both (a small player hugs a big one around the middle, a big one
# hugs a small one around the shoulders). They sway gently together and tip their heads past each other.
func _pose_hug(p):
	var frame = _pair_frame(p)
	var dir = frame[0]
	var mid = frame[1]
	var sa = _player_size(p["pa"])
	var sb = _player_size(p["pb"])
	var gap = 0.30 * (sa + sb) / 2.0           # chest to chest
	var sway = deg2rad(4.0) * sin(p["t"] * 2.2) * p["w"]
	_hug_one(p["pa"], p["pb"], dir, mid, gap, sa, sb, sway, p["w"], 1.0)
	_hug_one(p["pb"], p["pa"], -dir, mid, gap, sb, sa, sway, p["w"], -1.0)
	# little hearts rise over them now and then
	if not p["ending"] and p["t"] >= p["next_hearts"]:
		p["next_hearts"] = p["t"] + 1.6
		_hearts(p["pa"], Vector3(mid.x, max(_head_y(p["pa"]), _head_y(p["pb"])) + 0.8 * max(sa, sb), mid.z))


# The heads are huge (about 1.15 wide at size 1), so they can't both be in the middle: each body steps a
# little to its own left and each head tips further that way, so the heads go past each other side by
# side, the way two people rest their heads past each other's shoulders.
const HUG_SIDESTEP = 0.22   # times my size
const HUG_HEAD_TIP = 26.0   # degrees
func _hug_one(me_node, other, dir, mid, gap, s_me, s_other, sway, w, tilt_side):
	var left = Vector3.UP.cross(dir).normalized()
	var body = _place_body(me_node, dir, mid + left * HUG_SIDESTEP * s_me, gap, sway, w)
	if body == null:
		return
	var their_spot = mid + dir * gap / 2.0 - left * HUG_SIDESTEP * s_other  # they step to their left, my right
	var their_feet = other.global_transform.origin.y - 1.0 * s_other
	var height = min(_shoulder_y(me_node) - 0.12 * s_me, _shoulder_y(other) - 0.06 * s_other)
	height = max(height, their_feet + 0.35 * s_other)
	var radius = 0.24 * s_other                # roughly how round the other's body is
	var centre = Vector3(their_spot.x, height, their_spot.z) + dir * radius * 0.4
	_reach(body, 0, [centre, left * radius * 0.6], [Vector3.DOWN * 0.6 - dir * 0.2, left], left, w)
	_tilt_head_toward(body, dir, left, HUG_HEAD_TIP, w)


# A high five: both face each other an arm's length apart, raise their right hands and slap them
# together between them, a little above the shorter one's head, then let them drop again.
func _pose_highfive(p):
	var frame = _pair_frame(p)
	var dir = frame[0]
	var mid = frame[1]
	var sa = _player_size(p["pa"])
	var sb = _player_size(p["pb"])
	var gap = 0.46 * (sa + sb) / 2.0
	var height = (_shoulder_y(p["pa"]) + _shoulder_y(p["pb"])) / 2.0 + 0.16 * min(sa, sb)
	# no higher than the shorter one can reach, the taller one reaches down to meet them
	var short = p["pa"] if sa <= sb else p["pb"]
	height = min(height, _shoulder_y(short) + 0.3 * min(sa, sb))
	var contact = Vector3(mid.x, height, mid.z)
	var t = p["t"]
	# raise (0-0.35 s), swing in and slap (0.35-0.5 s), a little bounce back, hold, drop
	var swing = smoothstep(0.3, 0.5, t)
	var bounce = 0.04 * smoothstep(0.5, 0.65, t)
	if t >= 0.5 and not p["slapped"]:
		p["slapped"] = true
		_play_slap(p["pa"], contact)
	_five_one(p["pa"], dir, mid, gap, sa, contact, swing, bounce, p["w"])
	_five_one(p["pb"], -dir, mid, gap, sb, contact, swing, bounce, p["w"])


func _five_one(me_node, dir, mid, gap, s_me, contact, swing, bounce, w):
	var body = _place_body(me_node, dir, mid, gap, 0.0, w)
	if body == null:
		return
	var left = Vector3.UP.cross(dir).normalized()
	var right = -left
	# from up by my own shoulder, swung across to the meeting point
	var spot = body["spot"]
	var raised = Vector3(spot.x, _shoulder_y(me_node) + 0.3 * s_me, spot.z) + right * 0.18 * s_me + dir * 0.08 * s_me
	var target = raised.linear_interpolate(contact - dir * 0.03 * s_me, swing) - dir * bounce * s_me
	_reach(body, -1, target, right * 1.0 + Vector3.DOWN * 0.8, left, w)


# A kiss: they stand close, lean in towards each other (the taller one leans down further) until their
# faces meet in the middle, pucker up, and there's the game's kiss sound and a puff of hearts.
func _pose_kiss(p):
	var frame = _pair_frame(p)
	var dir = frame[0]
	var mid = frame[1]
	var sa = _player_size(p["pa"])
	var sb = _player_size(p["pb"])
	var lean = smoothstep(0.0, 0.5, p["t"])
	_kiss_one(p["pa"], dir, mid, sa, lean, p["w"])
	_kiss_one(p["pb"], -dir, mid, sb, lean, p["w"])
	if p["t"] >= 0.55 and not p["slapped"]:
		p["slapped"] = true
		var at = Vector3(mid.x, min(_head_y(p["pa"]), _head_y(p["pb"])) + 0.55 * min(sa, sb), mid.z)   # about where the faces meet
		_play_sound(p["pa"], kiss_sound, at, 1.0, 0.0)
		_hearts(p["pa"], at + Vector3.UP * 0.4 * min(sa, sb))
		for node in [p["pa"], p["pb"]]:
			if is_instance_valid(node) and node.has_method("_face_emote"):
				node._face_emote("kiss")


# The heads are huge (about 1.15 deep at size 1, the head bone being only the base of the head), so each
# one stands back from the middle by half a head plus a little, then leans forward just a little (10
# degrees), which brings the fronts of the two heads together.
const KISS_LEAN = 10.0
func _kiss_one(me_node, dir, mid, s_me, lean, w):
	var body = _place_body(me_node, dir, mid, 2.0 * 0.62 * s_me, 0.0, w)
	if body == null:
		return
	var sk = body["sk"]
	var torso = sk.find_bone("torso")
	var head = sk.find_bone("head")
	if torso < 0 or head < 0:
		return
	var g_torso = body["rebase"] * sk.get_bone_global_pose_no_override(torso)
	var g_head = body["rebase"] * sk.get_bone_global_pose_no_override(head)
	var axis = body["Winv"].basis.xform(Vector3.UP.cross(dir)).normalized()
	var toward = body["Winv"].basis.xform(dir).normalized()
	# whichever way round the axis is, tip towards the other one
	var deg = KISS_LEAN
	var tipped = (g_head.origin - g_torso.origin).rotated(axis, deg2rad(deg))
	if (tipped - (g_head.origin - g_torso.origin)).dot(toward) < 0:
		deg = -deg
	var tip = Basis(axis, deg2rad(deg * lean))
	sk.set_bone_global_pose_override(torso, Transform(tip * g_torso.basis, g_torso.origin), w, true)


func _head_y(player):
	var sk = player.get_node_or_null(SKELETON_PATH)
	if sk == null or sk.find_bone("head") < 0:
		return player.global_transform.origin.y + 0.7 * _player_size(player)
	return sk.global_transform.xform(sk.get_bone_global_pose_no_override(sk.find_bone("head")).origin).y


# a puff of the game's own hearts at a spot in the world
func _hearts(node, at):
	if hearts_scene == null or not is_instance_valid(node):
		return
	var fx = hearts_scene.instance()
	node.add_child(fx)
	fx.set_as_toplevel(true)
	fx.global_transform.origin = at
	fx.emitting = true
	get_tree().create_timer(fx.lifetime + 0.3).connect("timeout", fx, "queue_free")


func _play_slap(node, at):
	_play_sound(node, slap_sound, at, 1.5, -2.0)


func _play_sound(node, sound, at, pitch, db):
	if sound == null or not is_instance_valid(node):
		return
	var player = AudioStreamPlayer3D.new()
	player.stream = sound
	player.pitch_scale = pitch
	player.unit_db = db
	player.max_distance = 30.0
	node.add_child(player)
	player.global_transform.origin = at
	player.connect("finished", player, "queue_free")
	player.play()


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
		for name in ["pelvis", "torso", "head", "arm.l", "elbow.l", "arm.r", "elbow.r"]:
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


# nothing of the game's is open (my own picker window can be, when asked)
func _hud_free(allow_mine = false):
	var hud = _get_hud()
	if hud == null:
		return false
	if hud.get("menu") != 0 or hud.get("hud_hidden") == true or hud.get("using_chat") == true:
		return false
	var popups = hud.get("popups")
	if typeof(popups) == TYPE_ARRAY:
		for entry in popups:
			if not (allow_mine and entry == POPUP_ID):
				return false
	return true


# while the picker is open the game ignores its keys
func _lock_hud():
	var hud = _get_hud()
	if hud == null:
		return
	var popups = hud.get("popups")
	if typeof(popups) == TYPE_ARRAY and not popups.has(POPUP_ID):
		popups.append(POPUP_ID)
	locked_hud = hud


func _unlock_hud():
	if locked_hud != null and is_instance_valid(locked_hud):
		var popups = locked_hud.get("popups")
		if typeof(popups) == TYPE_ARRAY:
			popups.erase(POPUP_ID)
	locked_hud = null


# --- the invitation box ---

func _build_ui():
	# the prompt sits under the game's own windows, the picker and invitation on top
	prompt_layer = CanvasLayer.new()
	prompt_layer.layer = 1
	add_child(prompt_layer)
	layer = CanvasLayer.new()
	layer.layer = 3
	add_child(layer)

	# "F  Interact": small, in the same see-through glass as the Wolfpark HUD
	prompt = PanelContainer.new()
	prompt.visible = false
	prompt.mouse_filter = Control.MOUSE_FILTER_IGNORE
	prompt.add_stylebox_override("panel", _glass(Color(0.06, 0.12, 0.18, 0.55), Color(0.55, 1.0, 1.0, 0.45), 8, 3))
	prompt_layer.add_child(prompt)
	var prow = HBoxContainer.new()
	prow.add_constant_override("separation", 6)
	prow.mouse_filter = Control.MOUSE_FILTER_IGNORE
	prompt.add_child(prow)
	var key = Label.new()
	key.text = "F"
	key.add_color_override("font_color", Color(0.55, 1.0, 1.0))
	key.mouse_filter = Control.MOUSE_FILTER_IGNORE
	prow.add_child(key)
	var word = Label.new()
	word.text = "Interact"
	word.add_color_override("font_color", Color(1, 1, 1, 0.9))
	word.mouse_filter = Control.MOUSE_FILTER_IGNORE
	prow.add_child(word)
	prompt.rect_scale = Vector2(0.85, 0.85)

	# the picker: who it's for, one button per emote
	picker = PanelContainer.new()
	picker.visible = false
	picker.mouse_filter = Control.MOUSE_FILTER_STOP
	picker.add_stylebox_override("panel", _glass(Color(0.06, 0.12, 0.18, 0.7), Color(0.55, 1.0, 1.0, 0.45), 10, 6))
	layer.add_child(picker)
	var margin = MarginContainer.new()
	for side in ["left", "right"]:
		margin.add_constant_override("margin_" + side, 8)
	for side in ["top", "bottom"]:
		margin.add_constant_override("margin_" + side, 6)
	margin.mouse_filter = Control.MOUSE_FILTER_IGNORE
	picker.add_child(margin)
	var column = VBoxContainer.new()
	column.add_constant_override("separation", 4)
	column.mouse_filter = Control.MOUSE_FILTER_IGNORE
	margin.add_child(column)
	var head = HBoxContainer.new()
	head.add_constant_override("separation", 8)
	column.add_child(head)
	picker_title = Label.new()
	picker_title.add_color_override("font_color", Color(0.55, 1.0, 1.0))
	picker_title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	head.add_child(picker_title)
	var close = Button.new()
	close.text = "x"
	close.focus_mode = Control.FOCUS_NONE
	close.connect("pressed", self, "_close_picker")
	_glass_button(close)
	head.add_child(close)
	for kind in EMOTE_ORDER:
		var b = Button.new()
		b.text = EMOTES[kind]["label"]
		b.focus_mode = Control.FOCUS_NONE
		b.connect("pressed", self, "_on_pick", [kind])
		_glass_button(b)
		column.add_child(b)

	# the invitation
	invite_panel = PanelContainer.new()
	invite_panel.visible = false
	invite_panel.add_stylebox_override("panel", _glass(Color(0.06, 0.12, 0.18, 0.7), Color(0.55, 1.0, 1.0, 0.45), 10, 6))
	layer.add_child(invite_panel)
	var imargin = MarginContainer.new()
	for side in ["left", "right"]:
		imargin.add_constant_override("margin_" + side, 10)
	for side in ["top", "bottom"]:
		imargin.add_constant_override("margin_" + side, 6)
	invite_panel.add_child(imargin)
	var row = HBoxContainer.new()
	row.add_constant_override("separation", 8)
	imargin.add_child(row)
	invite_label = Label.new()
	invite_label.add_color_override("font_color", Color(1, 1, 1))
	invite_label.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	row.add_child(invite_label)
	invite_yes = Button.new()
	invite_yes.focus_mode = Control.FOCUS_NONE
	invite_yes.connect("pressed", self, "_on_invite_yes")
	_glass_button(invite_yes)
	row.add_child(invite_yes)
	var no = Button.new()
	no.text = "No thanks"
	no.focus_mode = Control.FOCUS_NONE
	no.connect("pressed", self, "_on_invite_no")
	_glass_button(no)
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


# the Wolfpark HUD's look: a see-through, softly outlined box, and its glassy buttons
func _glass(fill, edge, radius, shadow):
	var box = StyleBoxFlat.new()
	box.bg_color = fill
	box.border_color = edge
	box.set_border_width_all(1)
	box.set_corner_radius_all(radius)
	box.shadow_color = Color(0, 0, 0, 0.3)
	box.shadow_size = shadow
	box.content_margin_left = 8
	box.content_margin_right = 8
	box.content_margin_top = 3
	box.content_margin_bottom = 3
	return box


func _glass_button(button):
	var edge = Color(0.55, 1.0, 1.0, 0.5)
	button.add_stylebox_override("normal", _glass(Color(0.2, 0.9, 0.95, 0.16), edge, 8, 0))
	button.add_stylebox_override("hover", _glass(Color(0.3, 1.0, 1.0, 0.30), Color(0.8, 1.0, 1.0, 0.8), 8, 0))
	button.add_stylebox_override("pressed", _glass(Color(0.3, 1.0, 1.0, 0.45), Color(0.8, 1.0, 1.0, 0.9), 8, 0))
	button.add_stylebox_override("disabled", _glass(Color(0.5, 0.5, 0.5, 0.12), Color(0.7, 0.7, 0.7, 0.25), 8, 0))
	button.add_stylebox_override("focus", StyleBoxEmpty.new())
	button.add_color_override("font_color", Color(1, 1, 1))
	button.add_color_override("font_color_hover", Color(1, 1, 1))
	button.add_color_override("font_color_pressed", Color(1, 1, 1))
	button.add_color_override("font_color_disabled", Color(1, 1, 1, 0.4))
