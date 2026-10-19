extends Node

# Playground: the swing set
#
# Anyone with the mod can put up a two-seat swing set (Playground menu > Place a swing set); it stands a
# few steps in front of them, facing them, until they pick it up or leave. Walk up to a seat and press E
# to sit. The swing sways a little on its own. Press Space right at the top of each swing (front or back)
# to pump and go higher; a press at the wrong time costs a little height. At full height, Space at the
# top of the forward swing lets go and flings you across the sky like a blue mushroom. E gets off.
#
# The one sitting really moves: the mod puts their own character on the seat every frame and the game
# sends that to everyone as usual, so everybody sees them swinging (players without the mod see them
# sitting in mid air, swaying). Players with the mod also see the swing set and its seats move with them.
#
# Lobby member data:
#   "playground_set":   1;<x>;<y>;<z>;<yaw>;<zone>;<zone owner>     my swing set, where it stands
#   "playground_seat":  1;<owner's steam id>;<seat 0 or 1>           the seat I'm sitting on

const SET_KEY = "playground_set"
const SEAT_KEY = "playground_seat"
const SKELETON_PATH = "body/player_body/Armature/Skeleton"

const PIVOT_H = 3.4          # height of the top bar
const ROPE = 2.75            # from the bar down to the seat
const SEAT_X = [-0.85, 0.85] # the two seats along the bar
const FRAME_HALF = 2.05      # the A-frames at either end
const LEG_SPREAD = 1.25      # how far the legs reach out front and back
const PLACE_AHEAD = 3.2      # how far in front of me a new swing set stands

const PERIOD = 2.8           # seconds for a full swing there and back
const START_AMP = 10.0       # degrees: a little sway to begin with
const IDLE_AMP = 3.0         # an empty seat stirring
const MAX_AMP = 72.0         # as high as it goes
const PUMP = 9.0             # a well-timed press adds this much
const MISS = 4.0             # a press at the wrong moment loses this much
const FADE = 2.5             # degrees per second it dies down when you stop pumping
const WINDOW = 0.2           # how near the top a press counts, as a share of half a swing
const LAUNCH_FROM = MAX_AMP - PUMP   # "full height": within one pump of the top, you can let go
const SIT_LIFT = 1.03          # the player's origin above the seat board (times their size): sitting, their seat is at their feet' level, 1.0 below the origin, plus the board
const SIT_RANGE = 1.4        # how close to a seat to get the prompt
const LAUNCH_AHEAD = 24.0    # the fling: forward speed at full height
const LAUNCH_UP = 30.0       # and upwards (the blue mushroom gives 32 up)

var main                     # the Playground main node: its look and helpers
var clock = 0.0
var read_timer = 0.0
var next_check = 0.0
var sets = {}                # owner id -> {"pos", "yaw", "zone", "zone_owner", "node", "pivots", "angles"}
var seats = {}               # "<owner>:<seat>" -> steam id sitting there

# my swing set
var my_set_text = ""
# me on a swing
var seated_owner = 0
var seated_seat = -1
var phase = 0.0              # where in the swing (0..TAU); the front top is at PI/2, the back at 3PI/2
var tops = 0                 # how many half swings so far, to tell one top from the next
var amp = START_AMP          # how high it's going (degrees)
var amp_goal = START_AMP     # a pump raises this, and amp eases up to it
var pumped_top = -1          # the last top I pumped at, so one top counts once
var was_locked = false
var was_gravity = false
var seat_text = ""

# the screen
var prompt
var prompt_label
var prompt_owner = 0
var prompt_seat = -1
var hud_panel
var hint_label
var meter
var materials = {}


func _ready():
	process_priority = 100   # after the players have moved, so the seat has the last word
	_make_materials()
	_build_ui()


func _exit_tree():
	if seated_owner != 0:
		_stand_up(false)
	my_set_text = ""
	seat_text = ""
	_write(SET_KEY, "")
	_write(SEAT_KEY, "")


# --- placing and picking up ---

func has_set():
	return my_set_text != ""


func place_set():
	var me = _my_player()
	if me == null:
		return
	var size = main._player_size(me)
	var f = _facing(me)
	var feet = me.global_transform.origin - Vector3.UP * 1.0 * size
	var at = feet + f * PLACE_AHEAD
	at.y = _ground_y(at, feet.y)
	var yaw = atan2(f.x, f.z)   # the seats face me
	my_set_text = "1;%.2f;%.2f;%.2f;%.3f;%s;%d" % [at.x, at.y, at.z, yaw, str(me.get("current_zone")), int(me.get("current_zone_owner") if me.get("current_zone_owner") != null else -1)]
	_write(SET_KEY, my_set_text)
	main._show_toast("Swing set up! Walk to a seat and press E.")


func remove_set():
	my_set_text = ""
	_write(SET_KEY, "")
	main._show_toast("Swing set picked up.")


# down onto whatever is under that spot, so it doesn't float or sink on a slope
func _ground_y(at, fallback):
	var space = get_tree().root.get_world().direct_space_state if get_tree().root.get_world() != null else null
	if space == null:
		return fallback
	var hit = space.intersect_ray(at + Vector3.UP * 3.0, at + Vector3.DOWN * 6.0, [], 1)
	return hit["position"].y if hit.has("position") else fallback


# --- every frame ---

func _process(delta):
	clock += delta
	read_timer += delta
	if read_timer >= 0.2:
		read_timer = 0.0
		_read(main.steam)
	# make sure what I said stuck (a write made while joining a lobby is lost)
	if clock >= next_check:
		next_check = clock + 3.0
		if _read_mine(SET_KEY) != my_set_text:
			_write(SET_KEY, my_set_text)
		if _read_mine(SEAT_KEY) != seat_text:
			_write(SEAT_KEY, seat_text)
	_update_prompt()
	_update_hud()


func _physics_process(delta):
	var me = _my_player()
	for owner in sets.keys():
		_animate_set(owner, me, delta)
	if seated_owner != 0:
		_ride(me, delta)


func _read(steam):
	var lobby = _lobby()
	var wanted = {}
	var new_seats = {}
	var my_id = _my_id()
	if my_set_text != "":
		wanted[my_id] = my_set_text
	if lobby > 0 and steam != null and steam.has_method("getLobbyMemberData"):
		for player in get_tree().get_nodes_in_group("player"):
			var id = player.get("owner_id")
			if id == null or int(id) <= 0 or int(id) == my_id:
				continue
			var t = str(steam.getLobbyMemberData(lobby, int(id), SET_KEY))
			if t.begins_with("1;"):
				wanted[int(id)] = t
			var s = str(steam.getLobbyMemberData(lobby, int(id), SEAT_KEY)).split(";")
			if s.size() >= 3 and s[0] == "1":
				var key = s[1] + ":" + s[2]
				# two on one seat: the lower steam id keeps it
				if not new_seats.has(key) or int(id) < new_seats[key]:
					new_seats[key] = int(id)
	if seated_owner != 0:
		var mine = str(seated_owner) + ":" + str(seated_seat)
		if new_seats.has(mine) and new_seats[mine] < my_id:
			main._show_toast("Someone else took that seat.")
			_stand_up(false)
		else:
			new_seats[mine] = my_id
	seats = new_seats

	for owner in sets.keys():
		if not wanted.has(owner) or wanted[owner] != sets[owner]["text"]:
			if is_instance_valid(sets[owner]["node"]):
				sets[owner]["node"].queue_free()
			sets.erase(owner)
	for owner in wanted.keys():
		if not sets.has(owner):
			var s = _parse_set(wanted[owner])
			if s != null:
				s["text"] = wanted[owner]
				_build_set(owner, s)
				sets[owner] = s
	# the swing set I'm on went away
	if seated_owner != 0 and not sets.has(seated_owner):
		_stand_up(false)


func _parse_set(text):
	var b = text.split(";")
	if b.size() < 7:
		return null
	return {"pos": Vector3(float(b[1]), float(b[2]), float(b[3])), "yaw": float(b[4]), "zone": b[5],
		"zone_owner": int(b[6]), "angles": [0.0, 0.0]}


# --- the swing set itself, made from simple shapes ---

func _make_materials():
	materials["wood"] = _mat(Color(0.56, 0.37, 0.22))
	materials["dark"] = _mat(Color(0.42, 0.27, 0.16))
	materials["rope"] = _mat(Color(0.72, 0.72, 0.74))
	materials["seat"] = _mat(Color(0.25, 0.48, 0.86))


func _mat(c):
	var m = SpatialMaterial.new()
	m.albedo_color = c
	m.roughness = 1.0
	m.metallic_specular = 0.2
	return m


const MODEL = "res://mods/Playground/swing.scn"   # made in Blender: Frame, TopBar, Seat0 and Seat1 (hinged at the bar)
var model_scene = null

func _build_set(owner, s):
	var parent = _my_player().get_parent() if _my_player() != null else get_tree().root
	if model_scene == null and ResourceLoader.exists(MODEL):
		model_scene = load(MODEL)
	if model_scene != null:
		var model = model_scene.instance()
		model.name = "PlaygroundSwing"
		parent.add_child(model)
		model.global_transform = Transform(Basis(Vector3.UP, s["yaw"]), s["pos"])
		var seat0 = model.find_node("Seat0", true, false)
		var seat1 = model.find_node("Seat1", true, false)
		if seat0 != null and seat1 != null:
			s["pivots"] = [seat0, seat1]
			s["node"] = model
			return
		model.queue_free()
	# no model: the same swing set from simple shapes
	var root = Spatial.new()
	root.name = "PlaygroundSwing"
	parent.add_child(root)
	root.global_transform = Transform(Basis(Vector3.UP, s["yaw"]), s["pos"])
	# two A-frames and the top bar
	for x in [-FRAME_HALF, FRAME_HALF]:
		var top = Vector3(x, PIVOT_H, 0)
		for z in [-LEG_SPREAD, LEG_SPREAD]:
			_beam(root, top, Vector3(x * 1.06, 0, z), 0.075, materials["wood"])
		_beam(root, Vector3(x * 1.03, 0.9, -LEG_SPREAD * 0.74), Vector3(x * 1.03, 0.9, LEG_SPREAD * 0.74), 0.05, materials["dark"])
	_beam(root, Vector3(-FRAME_HALF - 0.15, PIVOT_H, 0), Vector3(FRAME_HALF + 0.15, PIVOT_H, 0), 0.09, materials["dark"])
	# the two seats, each hanging from its own pivot that swings
	s["pivots"] = []
	for x in SEAT_X:
		var pivot = Spatial.new()
		root.add_child(pivot)
		pivot.translation = Vector3(x, PIVOT_H, 0)
		for side in [-0.3, 0.3]:
			_beam(pivot, Vector3(side, 0, 0), Vector3(side, -ROPE, 0), 0.022, materials["rope"])
		var board = MeshInstance.new()
		var cube = CubeMesh.new()
		cube.size = Vector3(0.72, 0.07, 0.34)
		board.mesh = cube
		board.material_override = materials["seat"]
		board.translation = Vector3(0, -ROPE, 0)
		pivot.add_child(board)
		s["pivots"].append(pivot)
	s["node"] = root


# a round beam from a to b (in the parent's space)
func _beam(parent, a, b, radius, mat):
	var mi = MeshInstance.new()
	var cyl = CylinderMesh.new()
	cyl.top_radius = radius
	cyl.bottom_radius = radius
	cyl.height = a.distance_to(b)
	cyl.radial_segments = 8
	cyl.rings = 1
	mi.mesh = cyl
	mi.material_override = mat
	var up = (b - a).normalized()
	var side = up.cross(Vector3.FORWARD if abs(up.dot(Vector3.FORWARD)) < 0.9 else Vector3.RIGHT).normalized()
	var fwd = side.cross(up).normalized()
	mi.transform = Transform(Basis(side, up, fwd), (a + b) / 2.0)
	parent.add_child(mi)
	return mi


# where a seat's board is, in the world, at an angle (radians, + is towards the front)
func _seat_point(s, seat, angle):
	var root = s["node"]
	var local = Vector3(SEAT_X[seat], PIVOT_H - ROPE * cos(angle), -ROPE * sin(angle))
	return root.global_transform.xform(local)


func _animate_set(owner, me, delta):
	var s = sets[owner]
	if not is_instance_valid(s["node"]):
		return
	# only in the same place as me (the main island, the aquarium...)
	var here = true
	if me != null and me.get("current_zone") != null:
		here = str(me.get("current_zone")) == s["zone"]
	s["node"].visible = here
	for seat in range(2):
		var key = str(owner) + ":" + str(seat)
		var angle = deg2rad(IDLE_AMP) * sin(clock * TAU / PERIOD + seat * 1.7)
		if seated_owner == owner and seated_seat == seat:
			angle = deg2rad(amp) * sin(phase)
		elif seats.has(key):
			# someone else is on it: follow where the game shows them
			var rider = _player_of(seats[key])
			if rider != null:
				var size = main._player_size(rider)
				var local = s["node"].global_transform.affine_inverse().xform(rider.global_transform.origin - Vector3.UP * SIT_LIFT * size)
				var d = Vector2(-local.z, PIVOT_H - local.y)
				if d.length() > 0.5:
					angle = clamp(atan2(d.x, d.y), -deg2rad(85), deg2rad(85))
		s["angles"][seat] = lerp_angle(s["angles"][seat], angle, clamp(delta * 18.0, 0.0, 1.0))
		s["pivots"][seat].rotation = Vector3(s["angles"][seat], 0, 0)


# --- sitting, pumping, letting go ---

func sit(owner, seat):
	var me = _my_player()
	if me == null or not sets.has(owner) or seated_owner != 0:
		return
	seated_owner = owner
	seated_seat = seat
	phase = 0.0
	tops = 0
	amp = START_AMP
	amp_goal = START_AMP
	pumped_top = -1
	was_locked = me.get("locked") == true
	was_gravity = me.get("gravity_disable") == true
	seat_text = "1;" + str(owner) + ";" + str(seat)
	_write(SEAT_KEY, seat_text)


func _ride(me, delta):
	if me == null or not sets.has(seated_owner):
		return
	var s = sets[seated_owner]
	# it dies down slowly unless you keep pumping
	amp_goal = max(amp_goal - FADE * delta, START_AMP)
	amp = lerp(amp, amp_goal, clamp(delta * 3.0, 0.0, 1.0))
	var before = phase
	phase = fmod(phase + delta * TAU / PERIOD, TAU)
	# count half swings, each one passing the bottom (0 or PI)
	if int(floor(before / PI)) != int(floor(phase / PI)):
		tops += 1
	var angle = deg2rad(amp) * sin(phase)
	var size = main._player_size(me)
	var at = _seat_point(s, seated_seat, angle) + Vector3.UP * SIT_LIFT * size
	# the game: sitting, no walking or jumping of its own, no falling
	me.set("sitting", true)
	me.set("locked", true)
	me.set("gravity_disable", true)
	me.set("velocity", Vector3.ZERO)
	me.set("gravity_vec", Vector3.ZERO)
	me.global_transform.origin = at
	me.rotation.y = s["yaw"]


func _input(event):
	if not (event is InputEventKey) or not event.pressed or event.echo:
		return
	if main._typing():
		return
	if event.scancode == KEY_E:
		if seated_owner != 0:
			_stand_up(false)
			get_tree().set_input_as_handled()
		elif prompt_owner != 0 and prompt.visible:
			sit(prompt_owner, prompt_seat)
			get_tree().set_input_as_handled()
	elif event.scancode == KEY_SPACE and seated_owner != 0:
		_press()
		get_tree().set_input_as_handled()


# Space: right at a top of the swing it pumps (or, at full height at the front, lets go)
func _press():
	var half = fmod(phase, PI)              # 0..PI, the top is at PI/2
	var near = abs(half - PI / 2.0) <= WINDOW * PI
	var peak = int(floor(phase / PI))       # 0 = the front top, 1 = the back top
	if near and peak == 0 and amp_goal >= LAUNCH_FROM:
		_launch()
		return
	if near and tops != pumped_top:
		pumped_top = tops
		amp_goal = min(amp_goal + PUMP, MAX_AMP)
		_sfx("jump", 0.9 + amp_goal / MAX_AMP * 0.4)
	elif not near:
		amp_goal = max(amp_goal - MISS, START_AMP)


func _launch():
	var me = _my_player()
	if me == null:
		return
	var s = sets[seated_owner]
	var front = Basis(Vector3.UP, s["yaw"]).xform(Vector3(0, 0, -1))
	_stand_up(true)
	me.set("ignore_snap", 10)
	me.set("gravity_vec", front * LAUNCH_AHEAD + Vector3.UP * LAUNCH_UP)
	var data = me.get("animation_data")
	if typeof(data) == TYPE_DICTIONARY:
		data["mushroom"] = true
	_sfx("jump_big", 1.0)
	main._show_toast("Wheee!")


func _stand_up(launching):
	var me = _my_player()
	var owner = seated_owner
	var seat = seated_seat
	seated_owner = 0
	seated_seat = -1
	seat_text = ""
	_write(SEAT_KEY, "")
	if me == null or not is_instance_valid(me):
		return
	me.set("sitting", false)
	me.set("locked", was_locked)
	me.set("gravity_disable", was_gravity)
	if not launching and sets.has(owner):
		# step off the front of the seat, onto the ground
		var s = sets[owner]
		var size = main._player_size(me)
		var spot = s["node"].global_transform.xform(Vector3(SEAT_X[seat], 0, -0.9))
		spot.y = _ground_y(spot, s["pos"].y)
		me.global_transform.origin = spot + Vector3.UP * (1.0 * size + 0.1)


func _sfx(id, pitch):
	var me = _my_player()
	if me != null and me.has_method("_sync_sfx"):
		me._sync_sfx(id, null, pitch)


# --- "E  Sit" by an empty seat, and the swinging help at the bottom ---

func _update_prompt():
	prompt_owner = 0
	prompt_seat = -1
	var me = _my_player()
	if me == null or seated_owner != 0 or not main._hud_free():
		prompt.visible = false
		return
	var here = me.global_transform.origin
	var best = SIT_RANGE
	var best_at = Vector3()
	for owner in sets.keys():
		var s = sets[owner]
		if not is_instance_valid(s["node"]) or not s["node"].visible:
			continue
		for seat in range(2):
			if seats.has(str(owner) + ":" + str(seat)):
				continue
			var at = _seat_point(s, seat, s["angles"][seat])
			var d = Vector2(at.x - here.x, at.z - here.z).length()
			if d < best and abs(at.y - (here.y - 0.5)) < 1.6:
				best = d
				prompt_owner = owner
				prompt_seat = seat
				best_at = at
	if prompt_owner == 0:
		prompt.visible = false
		return
	_place_at(prompt, best_at + Vector3.UP * 0.9)


func _update_hud():
	if seated_owner == 0 or not main._hud_free(true):
		hud_panel.visible = false
		return
	hud_panel.visible = true
	var full = amp_goal >= LAUNCH_FROM
	if full:
		hint_label.text = "Full height! Space at the top of the front swing to let go.   E: get off"
	else:
		hint_label.text = "Space right at the top of each swing to go higher.   E: get off"
	meter.update()
	hud_panel.rect_size = Vector2()
	var size = hud_panel.get_viewport_rect().size
	var own = hud_panel.get_combined_minimum_size()
	hud_panel.rect_position = Vector2((size.x - own.x) / 2.0, size.y * 0.8)


# the swing as a dot sliding along a track: the ends light up when it's time to press
func _draw_meter():
	var box = meter.rect_size
	var y = box.y * 0.42
	var x0 = 10.0
	var x1 = box.x - 10.0
	meter.draw_line(Vector2(x0, y), Vector2(x1, y), Color(1, 1, 1, 0.35), 2.0)
	var half = fmod(phase, PI)
	var near = abs(half - PI / 2.0) <= WINDOW * PI
	var peak = int(floor(phase / PI))
	var zone_w = (x1 - x0) * 0.12
	for end in [0, 1]:
		# end 1 (right) is the front top, end 0 (left) the back
		var lit = near and ((end == 1 and peak == 0) or (end == 0 and peak == 1))
		var zx = x1 - zone_w if end == 1 else x0
		var col = Color(0.4, 1.0, 0.75, 0.85) if lit else Color(0.55, 1.0, 1.0, 0.25)
		if end == 1 and amp_goal >= LAUNCH_FROM:
			col = Color(1.0, 0.85, 0.4, 0.95 if lit else 0.45)
		meter.draw_rect(Rect2(zx, y - 6, zone_w, 12), col)
	var t = sin(phase)                      # -1 back .. +1 front
	var reach = amp / MAX_AMP
	var height = amp_goal / MAX_AMP
	var dot = lerp(x0, x1, 0.5 + 0.5 * t * reach)
	meter.draw_circle(Vector2(dot, y), 6.0, Color(1, 1, 1, 0.95))
	# how high: a thin bar underneath
	meter.draw_rect(Rect2(x0, box.y - 6, (x1 - x0), 3), Color(1, 1, 1, 0.15))
	meter.draw_rect(Rect2(x0, box.y - 6, (x1 - x0) * height, 3), Color(1.0, 0.85, 0.4, 0.9))


func _build_ui():
	prompt = main._panel(0.55)
	prompt.visible = false
	prompt.mouse_filter = Control.MOUSE_FILTER_IGNORE
	var row = HBoxContainer.new()
	row.add_constant_override("separation", 6)
	row.mouse_filter = Control.MOUSE_FILTER_IGNORE
	prompt.add_child(row)
	var key = Label.new()
	key.text = "E"
	key.add_color_override("font_color", Color(0.55, 1.0, 1.0))
	row.add_child(key)
	prompt_label = Label.new()
	prompt_label.text = "Sit on the swing"
	prompt_label.add_color_override("font_color", Color(1, 1, 1, 0.9))
	row.add_child(prompt_label)
	prompt.rect_scale = Vector2(0.85, 0.85)

	hud_panel = main._panel(0.6)
	hud_panel.visible = false
	hud_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	var col = main._column(hud_panel)
	meter = Control.new()
	meter.rect_min_size = Vector2(300, 30)
	meter.mouse_filter = Control.MOUSE_FILTER_IGNORE
	meter.connect("draw", self, "_draw_meter")
	col.add_child(meter)
	hint_label = Label.new()
	hint_label.align = Label.ALIGN_CENTER
	hint_label.add_color_override("font_color", Color(1, 1, 1, 0.9))
	col.add_child(hint_label)
	main.themed.append(prompt)
	main.themed.append(hud_panel)


# a box on screen just above a point in the world
func _place_at(control, world_pos):
	var me = _my_player()
	var view = me.get_viewport() if me != null else null
	var camera = view.get_camera() if view != null else null
	if camera == null or camera.is_position_behind(world_pos):
		control.visible = false
		return
	var at = camera.unproject_position(world_pos)
	var holder = view.get_parent()
	if holder is ViewportContainer and view.size.x > 0 and view.size.y > 0:
		var box = holder.get_global_rect()
		at = box.position + at * (box.size / view.size)
	control.visible = true
	control.rect_size = Vector2()
	var size = control.get_combined_minimum_size() * control.rect_scale
	control.rect_position = Vector2(at.x - size.x / 2.0, at.y - size.y)


# --- helpers ---

func _my_player():
	var list = get_tree().get_nodes_in_group("controlled_player")
	return list[0] if not list.empty() and is_instance_valid(list[0]) else null


func _player_of(id):
	for player in get_tree().get_nodes_in_group("player"):
		if is_instance_valid(player) and player.get("owner_id") != null and int(player.get("owner_id")) == int(id):
			return player
	return null


func _facing(node):
	var f = -node.global_transform.basis.z
	f.y = 0
	return f.normalized() if f.length() > 0.001 else Vector3(0, 0, -1)


func _lobby():
	var net = get_node_or_null("/root/Network")
	if net != null and not net.PLAYING_OFFLINE and net.STEAM_LOBBY_ID > 0:
		return net.STEAM_LOBBY_ID
	return 0


func _my_id():
	var net = get_node_or_null("/root/Network")
	return int(net.STEAM_ID) if net != null else 0


func _read_mine(key):
	var lobby = _lobby()
	if lobby <= 0 or main.steam == null or not main.steam.has_method("getLobbyMemberData"):
		return ""
	return str(main.steam.getLobbyMemberData(lobby, _my_id(), key))


func _write(key, text):
	var lobby = _lobby()
	if lobby <= 0 or main == null or main.steam == null or not main.steam.has_method("setLobbyMemberData"):
		return
	main.steam.setLobbyMemberData(lobby, key, text)
