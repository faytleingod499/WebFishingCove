extends Node

# Wolf Pets
# A little wolf that follows you around: it walks after you, waits next to you and sits down when
# you stand still for a while. Click the paw button to show or hide it and to pick its colours.
# Your colours are shared through the Steam lobby, so other players who have this mod see your wolf
# exactly as you made it. Players without the mod do not see pets at all. Works on any server.

const VERSION = "1.1.0"
const CONFIG_PATH = "user://wolfpets.json"
const MODEL_PATH = "res://mods/WolfPets/wolf_pet.scn"
const SHADOW_PATH = "res://Scenes/Entities/Shadow/shadow.tscn"
const SYNC_KEY = "wolfpet"
const POPUP_ID = "WOLFPETS"
const DEFAULT_PAW_POS = Vector2(16, 140)

const PET_SCALE = 0.82    # about 60% of a player's height
const FOLLOW_NEAR = 2.1    # stops when this close to its owner
const FOLLOW_FAR = 3.0     # starts walking again when further than this
const MAX_PALETTES = 12
const TELEPORT_DIST = 16.0 # this far behind it just appears next to its owner again
const WALK_SPEED = 3.0
const RUN_SPEED = 7.5
const SIT_AFTER = 5.0      # seconds of standing still before it sits down

# the parts that can be coloured: key, label, the model's material for it
const PARTS = [
	["fur", "Fur", "wolf_fur"],
	["back", "Back", "wolf_dark"],
	["belly", "Belly & paws", "wolf_light"],
	["eyes", "Eyes", "wolf_eye"],
	["nose", "Nose & mouth", "wolf_black"],
	["ears", "Ears & tongue", "wolf_ear_inner"],
]
const PRESETS = {
	"Grey": {"fur": "aaadb8", "back": "797c89", "belly": "e7eaed", "eyes": "303038", "nose": "303038", "ears": "e7bfc8"},
	"White": {"fur": "eff0f2", "back": "d4d8df", "belly": "fcfcfa", "eyes": "303038", "nose": "303038", "ears": "e7bfc8"},
	"Black": {"fur": "4d4d55", "back": "2c2c30", "belly": "95979e", "eyes": "e8a23a", "nose": "1c1c20", "ears": "e7bfc8"},
}
const PRESET_ORDER = ["Grey", "White", "Black"]

var steam = null
var model_scene = null
var shadow_scene = null

# my pet
var show_pet = true
var colors = {}
var sync_dirty = true
var sync_timer = 0.0
var synced_lobby = 0

# every pet in the world: key -> {node, model, anim, meshes, mats, speed, still, anim_name, sig}
var pets = {}
var remote_cfg = {} # steam id -> {"show": bool, "colors": {}, "raw": text}
var read_timer = 0.0

# the paw button and the menu
var layer
var paw
var paw_hover = false
var paw_pressed = false
var paw_dragging = false
var press_pos = Vector2()
var drag_offset = Vector2()
var menu
var show_box
var pickers = {}
var menu_open = false
var palettes = {} # name -> colours, the player's own saved looks
var name_field
var palette_box
var locked_hud = null
var theme_applied = false


func _ready():
	if Engine.has_singleton("Steam"):
		steam = Engine.get_singleton("Steam")
	model_scene = load(MODEL_PATH)
	if ResourceLoader.exists(SHADOW_PATH):
		shadow_scene = load(SHADOW_PATH)
	_reset_colors("Grey")
	_build_ui()
	_load_config()
	_rebuild_palettes()
	_apply_to_pickers()


func _exit_tree():
	_unlock_hud()
	for key in pets.keys():
		_remove_pet(key)


# --- colours ---

func _reset_colors(preset):
	colors = {}
	for part in PARTS:
		colors[part[0]] = PRESETS[preset][part[0]]


func _color_of(cfg_colors, key):
	var text = str(cfg_colors.get(key, ""))
	if not text.is_valid_html_color():
		text = PRESETS["Grey"][key]
	return Color(text)


# what goes into the lobby: "1;1;fur,back,belly,eyes,nose,ears" (format, shown, colours)
func _sync_text():
	var list = []
	for part in PARTS:
		list.append(str(colors[part[0]]))
	return "1;" + ("1" if show_pet else "0") + ";" + PoolStringArray(list).join(",")


func _parse_sync(text):
	var bits = str(text).split(";")
	if bits.size() < 3 or bits[0] != "1":
		return null
	var cfg = {"show": bits[1] == "1", "colors": {}, "raw": text}
	var list = bits[2].split(",")
	for i in range(PARTS.size()):
		if i < list.size() and list[i].is_valid_html_color():
			cfg["colors"][PARTS[i][0]] = list[i]
	return cfg


# --- every frame ---

func _process(delta):
	_refresh_ui()

	var net = get_node_or_null("/root/Network")
	var lobby = 0
	if net != null and not net.PLAYING_OFFLINE and net.STEAM_LOBBY_ID > 0:
		lobby = net.STEAM_LOBBY_ID

	# tell the lobby about my pet (not more than once a second, colour dragging changes it a lot)
	sync_timer += delta
	if lobby != synced_lobby:
		synced_lobby = lobby
		sync_dirty = true
	if sync_dirty and sync_timer >= 1.0 and lobby > 0 and steam != null and steam.has_method("setLobbyMemberData"):
		sync_timer = 0.0
		sync_dirty = false
		steam.setLobbyMemberData(lobby, SYNC_KEY, _sync_text())

	# read everyone else's pets once a second
	read_timer += delta
	if read_timer >= 1.0:
		read_timer = 0.0
		_read_remote(lobby)

	_update_pets(delta, net)


func _read_remote(lobby):
	remote_cfg = {}
	if lobby <= 0 or steam == null or not steam.has_method("getLobbyMemberData"):
		return
	for player in get_tree().get_nodes_in_group("player"):
		if not _is_real_player(player) or player.get("controlled") == true:
			continue
		var id = player.get("owner_id")
		var cfg = _parse_sync(steam.getLobbyMemberData(lobby, id, SYNC_KEY))
		if cfg != null:
			remote_cfg[id] = cfg


func _is_real_player(player):
	if not is_instance_valid(player):
		return false
	if player.get("dead_actor") == true or player.get("NPC_body") == true:
		return false
	var id = player.get("owner_id")
	return id != null and int(id) > 0


func _update_pets(delta, net):
	var wanted = {} # pet key -> [owner node, show, colours]
	for player in get_tree().get_nodes_in_group("player"):
		if not _is_real_player(player):
			continue
		if player.get("controlled") == true:
			wanted["me"] = [player, show_pet, colors]
		else:
			var id = player.get("owner_id")
			if remote_cfg.has(id) and remote_cfg[id]["show"]:
				wanted[id] = [player, true, remote_cfg[id]["colors"]]

	for key in pets.keys():
		var pet = pets[key]
		if not wanted.has(key) or not wanted[key][1] or not is_instance_valid(pet["node"]) \
				or not is_instance_valid(pet["owner"]) or pet["owner"] != wanted[key][0]:
			_remove_pet(key)

	if model_scene == null:
		return
	for key in wanted.keys():
		var owner_node = wanted[key][0]
		if not wanted[key][1]:
			continue
		if not pets.has(key):
			var pet = _make_pet(owner_node)
			if pet == null:
				continue
			pets[key] = pet
		var pet = pets[key]
		var sig = str(wanted[key][2].hash())
		if pet["sig"] != sig:
			pet["sig"] = sig
			_paint(pet, wanted[key][2])
		_move_pet(pet, owner_node, delta)


# --- making a pet ---

func _make_pet(owner_node):
	var parent = owner_node.get_parent()
	if parent == null:
		return null
	var node = Spatial.new()
	node.name = "WolfPet"
	parent.add_child(node)

	var model = model_scene.instance()
	model.rotation_degrees = Vector3(0, 180, 0) # the model looks along +Z, Godot's forward is -Z
	model.scale = Vector3.ONE * PET_SCALE
	node.add_child(model)

	if shadow_scene != null:
		var shadow = shadow_scene.instance()
		node.add_child(shadow)

	var anim = model.find_node("AnimationPlayer", true, false)
	var meshes = []
	_collect_meshes(model, meshes)

	# each pet gets its own materials so everyone's wolf can have different colours
	var mats = {}
	for mi in meshes:
		for i in range(mi.mesh.get_surface_count()):
			var base = mi.mesh.surface_get_material(i)
			if base == null:
				continue
			var own = base.duplicate()
			mi.set_surface_material(i, own)
			mats[base.resource_name] = own

	# start next to its owner
	var start = owner_node.global_transform.origin + Vector3(1.0, 0, 1.0).normalized() * FOLLOW_NEAR
	node.global_transform.origin = _ground(node, start, owner_node.global_transform.origin.y)

	var pet = {"node": node, "model": model, "anim": anim, "mats": mats, "owner": owner_node,
		"speed": 0.0, "still": 0.0, "anim_name": "", "sig": "", "moving": false}
	_play(pet, "wolf_idle", 1.0)
	return pet


func _collect_meshes(node, out):
	if node is MeshInstance and node.mesh != null:
		out.append(node)
	for child in node.get_children():
		_collect_meshes(child, out)


func _paint(pet, cfg_colors):
	for part in PARTS:
		var mat = pet["mats"].get(part[2])
		if mat != null and mat is SpatialMaterial:
			mat.albedo_color = _color_of(cfg_colors, part[0])


func _remove_pet(key):
	if not pets.has(key):
		return
	var node = pets[key]["node"]
	if is_instance_valid(node):
		node.queue_free()
	pets.erase(key)


# --- moving a pet ---

func _move_pet(pet, owner_node, delta):
	var node = pet["node"]
	node.visible = owner_node.is_visible_in_tree()

	var target = owner_node.global_transform.origin
	var pos = node.global_transform.origin
	var flat = Vector3(target.x - pos.x, 0, target.z - pos.z)
	var dist = flat.length()

	if dist > TELEPORT_DIST:
		var back = Vector3(1.0, 0, 1.0).normalized() * FOLLOW_NEAR
		node.global_transform.origin = _ground(node, target + back, target.y)
		pet["speed"] = 0.0
		pet["still"] = 0.0
		pet["moving"] = false
		_play(pet, "wolf_idle", 1.0)
		return

	# walk when it fell behind, stop again once it is close; it hurries when far away
	if dist > FOLLOW_FAR:
		pet["moving"] = true
	elif dist < FOLLOW_NEAR:
		pet["moving"] = false
	var want = 0.0
	if pet["moving"]:
		want = clamp((dist - FOLLOW_NEAR) * 2.2, WALK_SPEED * 0.6, RUN_SPEED)
	pet["speed"] = lerp(pet["speed"], want, clamp(delta * 6.0, 0.0, 1.0))

	var dir = flat / dist if dist > 0.001 else Vector3()
	if pet["speed"] > 0.05:
		pos += dir * min(pet["speed"] * delta, dist)
	pos = _ground(node, pos, target.y, pos.y, delta)
	node.global_transform.origin = pos

	# turn to where it walks, or to its owner while standing
	if dist > 0.05:
		var yaw = atan2(-dir.x, -dir.z)
		node.rotation.y = lerp_angle(node.rotation.y, yaw, clamp(delta * (8.0 if pet["speed"] > 0.3 else 3.0), 0.0, 1.0))

	# idle, walk, and sit after a while of standing still
	if pet["speed"] > 0.35:
		pet["still"] = 0.0
		# the legs speed up with it, quicker still when it runs
		var rate = clamp(0.6 + pet["speed"] * 0.45, 0.8, 4.2)
		_play(pet, "wolf_walk", rate)
	else:
		pet["still"] += delta
		_play(pet, "wolf_sit" if pet["still"] > SIT_AFTER else "wolf_idle", 1.0)


# the floor under a spot; when there is none it keeps to its owner's height
func _ground(node, pos, fallback_y, current_y = null, delta = 0.0):
	var y = fallback_y
	var space = node.get_world().direct_space_state if node.get_world() != null else null
	if space != null:
		var hit = space.intersect_ray(pos + Vector3(0, 1.5, 0), pos + Vector3(0, -4.0, 0), [], 1 | 2)
		if not hit.empty():
			y = hit["position"].y
	if current_y != null:
		y = lerp(current_y, y, clamp(delta * 12.0, 0.0, 1.0))
	return Vector3(pos.x, y, pos.z)


func _play(pet, name, rate):
	var anim = pet["anim"]
	if anim == null:
		return
	anim.playback_speed = rate
	if pet["anim_name"] != name and anim.has_animation(name):
		pet["anim_name"] = name
		anim.play(name, 0.3)


# --- the paw button ---

func _build_ui():
	layer = CanvasLayer.new()
	layer.layer = 3
	add_child(layer)

	paw = Control.new()
	paw.rect_size = Vector2(34, 34)
	paw.rect_min_size = Vector2(34, 34)
	paw.rect_position = DEFAULT_PAW_POS
	paw.mouse_filter = Control.MOUSE_FILTER_STOP
	paw.visible = false
	paw.connect("draw", self, "_draw_paw")
	paw.connect("gui_input", self, "_on_paw_input")
	paw.connect("mouse_entered", self, "_on_paw_hover", [true])
	paw.connect("mouse_exited", self, "_on_paw_hover", [false])
	layer.add_child(paw)
	_add_tooltip(paw, "Pet", "Click for your pet, drag to move this button")

	menu = PanelContainer.new()
	menu.visible = false
	menu.mouse_filter = Control.MOUSE_FILTER_STOP
	menu.add_stylebox_override("panel", _glass(Color(0.06, 0.12, 0.18, 0.75), Color(0.55, 1.0, 1.0, 0.45), 10, 6))
	layer.add_child(menu)

	var margin = MarginContainer.new()
	for side in ["left", "right", "top", "bottom"]:
		margin.add_constant_override("margin_" + side, 8)
	menu.add_child(margin)

	var column = VBoxContainer.new()
	column.add_constant_override("separation", 6)
	margin.add_child(column)

	var head = HBoxContainer.new()
	column.add_child(head)
	var title = Label.new()
	title.text = "Your Pet"
	title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	title.add_color_override("font_color", Color(0.75, 0.95, 1.0))
	head.add_child(title)
	var close = Button.new()
	close.text = "x"
	close.focus_mode = Control.FOCUS_NONE
	close.connect("pressed", self, "_set_menu", [false])
	_glass_button(close)
	head.add_child(close)

	show_box = CheckBox.new()
	show_box.text = "Show my wolf"
	show_box.focus_mode = Control.FOCUS_NONE
	show_box.add_color_override("font_color", Color(1, 1, 1))
	show_box.add_color_override("font_color_hover", Color(1, 1, 1))
	show_box.add_color_override("font_color_pressed", Color(1, 1, 1))
	show_box.connect("toggled", self, "_on_show_toggled")
	column.add_child(show_box)

	var preset_row = HBoxContainer.new()
	preset_row.add_constant_override("separation", 6)
	column.add_child(preset_row)
	for name in PRESET_ORDER:
		var b = Button.new()
		b.text = name
		b.focus_mode = Control.FOCUS_NONE
		b.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		b.connect("pressed", self, "_on_preset", [name])
		_glass_button(b)
		preset_row.add_child(b)

	var grid = GridContainer.new()
	grid.columns = 2
	grid.add_constant_override("hseparation", 10)
	grid.add_constant_override("vseparation", 4)
	column.add_child(grid)
	for part in PARTS:
		var label = Label.new()
		label.text = part[1]
		label.add_color_override("font_color", Color(1, 1, 1))
		grid.add_child(label)
		var picker = ColorPickerButton.new()
		picker.rect_min_size = Vector2(64, 22)
		picker.edit_alpha = false
		picker.focus_mode = Control.FOCUS_NONE
		picker.connect("color_changed", self, "_on_color_changed", [part[0]])
		picker.connect("popup_closed", self, "_save_config")
		grid.add_child(picker)
		pickers[part[0]] = picker

	# saving the current colours under a name
	var save_row = HBoxContainer.new()
	save_row.add_constant_override("separation", 6)
	column.add_child(save_row)
	name_field = LineEdit.new()
	name_field.placeholder_text = "palette name"
	name_field.max_length = 20
	name_field.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	name_field.add_stylebox_override("normal", _glass(Color(0, 0, 0, 0.30), Color(0.55, 1.0, 1.0, 0.35), 8, 0))
	name_field.add_stylebox_override("focus", _glass(Color(0, 0, 0, 0.40), Color(0.8, 1.0, 1.0, 0.9), 8, 0))
	name_field.add_color_override("font_color", Color(1, 1, 1))
	name_field.connect("text_entered", self, "_on_save_palette")
	save_row.add_child(name_field)
	var save = Button.new()
	save.text = "Save"
	save.focus_mode = Control.FOCUS_NONE
	save.connect("pressed", self, "_on_save_palette")
	_glass_button(save)
	save_row.add_child(save)

	var saved_label = Label.new()
	saved_label.text = "My palettes"
	saved_label.add_color_override("font_color", Color(0.75, 0.95, 1.0))
	column.add_child(saved_label)
	palette_box = VBoxContainer.new()
	palette_box.add_constant_override("separation", 3)
	column.add_child(palette_box)


func _draw_paw():
	var box = paw.rect_size
	var fill = Color(0.3, 1.0, 1.0, 0.30 if paw_hover else 0.16)
	if menu_open:
		fill = Color(0.3, 1.0, 1.0, 0.45)
	paw.draw_style_box(_glass(fill, Color(0.55, 1.0, 1.0, 0.8 if paw_hover else 0.5), 8, 0), Rect2(Vector2(), box))
	var c = Color(1, 1, 1, 0.95)
	var mid = box / 2.0 + Vector2(0, 3)
	# the big pad, a little wider than tall
	paw.draw_set_transform(mid, 0.0, Vector2(1.2, 0.95))
	paw.draw_circle(Vector2(), 6.0, c)
	paw.draw_set_transform(Vector2(), 0.0, Vector2(1, 1))
	# four toes
	for toe in [Vector2(-8.5, -6.5), Vector2(-3.2, -11.0), Vector2(3.2, -11.0), Vector2(8.5, -6.5)]:
		paw.draw_circle(mid + toe, 2.7, c)


func _on_paw_hover(inside):
	paw_hover = inside
	paw.update()


func _on_paw_input(event):
	if event is InputEventMouseButton and event.button_index == BUTTON_LEFT:
		if event.pressed:
			paw_pressed = true
			paw_dragging = false
			press_pos = event.global_position
			drag_offset = paw.rect_position - press_pos
			paw.accept_event()


# dragging is followed here so it keeps going when the mouse leaves the button
func _input(event):
	if paw_pressed:
		if event is InputEventMouseMotion:
			var mouse = event.position
			if not paw_dragging and mouse.distance_to(press_pos) > 4.0:
				paw_dragging = true
			if paw_dragging:
				var area = get_viewport().get_visible_rect().size
				var pos = mouse + drag_offset
				pos.x = clamp(pos.x, 0, max(0, area.x - paw.rect_size.x))
				pos.y = clamp(pos.y, 0, max(0, area.y - paw.rect_size.y))
				paw.rect_position = pos
				_place_menu()
		elif event is InputEventMouseButton and event.button_index == BUTTON_LEFT and not event.pressed:
			paw_pressed = false
			if paw_dragging:
				paw_dragging = false
				_save_config()
			else:
				_set_menu(not menu_open)
		return

	if menu_open and event is InputEventKey and event.pressed and event.scancode == KEY_ESCAPE:
		_set_menu(false)
		get_tree().set_input_as_handled()


func _set_menu(open):
	if menu_open == open:
		return
	menu_open = open
	menu.visible = open
	if open:
		_apply_to_pickers()
		_place_menu()
		_lock_hud()
	else:
		for key in pickers.keys():
			var popup = pickers[key].get_popup()
			if popup != null and popup.visible:
				popup.hide()
		_unlock_hud()
		_save_config()
	paw.update()


# the menu opens beside the paw, on whichever side has room
func _place_menu():
	if not menu.visible:
		return
	var area = get_viewport().get_visible_rect().size
	var size = menu.get_combined_minimum_size()
	menu.rect_size = size
	var pos = paw.rect_position + Vector2(paw.rect_size.x + 6, 0)
	if pos.x + size.x > area.x:
		pos.x = paw.rect_position.x - size.x - 6
	pos.x = clamp(pos.x, 0, max(0, area.x - size.x))
	pos.y = clamp(pos.y, 0, max(0, area.y - size.y))
	menu.rect_position = pos


func _apply_to_pickers():
	if show_box != null:
		show_box.set_block_signals(true)
		show_box.pressed = show_pet
		show_box.set_block_signals(false)
	for key in pickers.keys():
		pickers[key].color = Color(colors[key])


func _on_show_toggled(on):
	show_pet = on
	sync_dirty = true
	_save_config()


func _on_preset(name):
	_reset_colors(name)
	_apply_to_pickers()
	sync_dirty = true
	_save_config()


func _on_save_palette(_text = ""):
	var name = name_field.text.strip_edges()
	if name == "":
		name_field.placeholder_text = "type a name first"
		return
	if not palettes.has(name) and palettes.size() >= MAX_PALETTES:
		name_field.text = ""
		name_field.placeholder_text = "full, delete one first"
		return
	palettes[name] = colors.duplicate()
	name_field.text = ""
	name_field.placeholder_text = "palette name"
	name_field.release_focus()
	_rebuild_palettes()
	_save_config()


func _on_use_palette(name):
	if not palettes.has(name):
		return
	for part in PARTS:
		var text = str(palettes[name].get(part[0], ""))
		if text.is_valid_html_color():
			colors[part[0]] = text
	name_field.text = name # so saving again updates it
	_apply_to_pickers()
	sync_dirty = true
	_save_config()


func _on_delete_palette(name):
	palettes.erase(name)
	_rebuild_palettes()
	_save_config()


# one row per saved palette: its colours as little swatches, its name, and a delete button
func _rebuild_palettes():
	if palette_box == null:
		return
	for child in palette_box.get_children():
		palette_box.remove_child(child)
		child.queue_free()
	var names = palettes.keys()
	names.sort()
	if names.empty():
		var none = Label.new()
		none.text = "none saved yet"
		none.add_color_override("font_color", Color(1, 1, 1, 0.5))
		palette_box.add_child(none)
	for name in names:
		var row = HBoxContainer.new()
		row.add_constant_override("separation", 4)
		palette_box.add_child(row)
		var use = Button.new()
		use.text = name
		use.clip_text = true
		use.align = Button.ALIGN_LEFT
		use.focus_mode = Control.FOCUS_NONE
		use.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		use.connect("pressed", self, "_on_use_palette", [name])
		_glass_button(use)
		row.add_child(use)
		for key in ["fur", "back", "belly", "eyes"]:
			var swatch = ColorRect.new()
			swatch.rect_min_size = Vector2(8, 18)
			swatch.color = _color_of(palettes[name], key)
			swatch.mouse_filter = Control.MOUSE_FILTER_IGNORE
			row.add_child(swatch)
		var del = Button.new()
		del.text = "x"
		del.focus_mode = Control.FOCUS_NONE
		del.connect("pressed", self, "_on_delete_palette", [name])
		_glass_button(del)
		row.add_child(del)
	if menu_open:
		call_deferred("_place_menu")


func _on_color_changed(color, key):
	colors[key] = color.to_html(false)
	sync_dirty = true


# like the game's own hotbar: only there while no menu of the game is open
func _refresh_ui():
	var hud = _get_hud()
	var show = hud != null
	if show:
		if hud.get("menu") != 0 or hud.get("hud_hidden") == true:
			show = false
		var popups = hud.get("popups")
		if typeof(popups) == TYPE_ARRAY:
			for entry in popups:
				if entry != POPUP_ID:
					show = false
		var options = get_node_or_null("/root/OptionsMenu")
		if options != null and options.get("open") == true:
			show = false
	if show and not theme_applied:
		theme_applied = true
		var main = hud.get_node_or_null("main")
		if main != null and main.theme != null:
			paw.theme = main.theme
			menu.theme = main.theme
	if not show and menu_open:
		_set_menu(false)
	if not show:
		paw_pressed = false
		paw_dragging = false
	paw.visible = show


func _get_hud():
	var players = get_tree().get_nodes_in_group("controlled_player")
	if players.empty():
		return null
	var hud = players[0].get("hud")
	if hud == null or not is_instance_valid(hud):
		return null
	return hud


# while the menu is open the game must not react to keys (typing a colour code would change the hotbar)
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


# --- looks ---

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
	button.add_stylebox_override("focus", StyleBoxEmpty.new())
	button.add_color_override("font_color", Color(1, 1, 1))
	button.add_color_override("font_color_hover", Color(1, 1, 1))
	button.add_color_override("font_color_pressed", Color(1, 1, 1))


# the game's own hover box, so it looks like the rest of the game
func _add_tooltip(control, header, body):
	var scene = load("res://Scenes/Singletons/Tooltips/tooltip_node.tscn") if ResourceLoader.exists("res://Scenes/Singletons/Tooltips/tooltip_node.tscn") else null
	if scene == null or get_node_or_null("/root/Tooltip") == null:
		control.hint_tooltip = header
		return
	var tip = scene.instance()
	tip.header = "[color=#6a4420]" + header + "[/color]"
	tip.body = body
	tip.mouse_filter = Control.MOUSE_FILTER_PASS
	control.add_child(tip)


# --- saving ---

func _load_config():
	var file = File.new()
	if not file.file_exists(CONFIG_PATH) or file.open(CONFIG_PATH, File.READ) != OK:
		return
	var data = parse_json(file.get_as_text())
	file.close()
	if typeof(data) != TYPE_DICTIONARY:
		return
	if data.has("x") and data.has("y"):
		paw.rect_position = Vector2(float(data["x"]), float(data["y"]))
	if data.has("show"):
		show_pet = bool(data["show"])
	if typeof(data.get("palettes")) == TYPE_DICTIONARY:
		for name in data["palettes"].keys():
			if typeof(data["palettes"][name]) == TYPE_DICTIONARY and palettes.size() < MAX_PALETTES:
				palettes[str(name)] = data["palettes"][name]
	_rebuild_palettes()
	if typeof(data.get("colors")) == TYPE_DICTIONARY:
		for part in PARTS:
			var text = str(data["colors"].get(part[0], ""))
			if text.is_valid_html_color():
				colors[part[0]] = text
	sync_dirty = true


func _save_config():
	var file = File.new()
	if file.open(CONFIG_PATH, File.WRITE) != OK:
		return
	file.store_string(to_json({"x": paw.rect_position.x, "y": paw.rect_position.y, "show": show_pet, "colors": colors, "palettes": palettes}))
	file.close()
