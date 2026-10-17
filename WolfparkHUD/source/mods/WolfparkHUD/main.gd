extends Node

# Wolfpark HUD
# A small panel with your coin balance and buttons for the server's commands.
# It only appears on servers that announce it (the Cove "Feed" plugin does this through the
# lobby data), on every other server this mod does nothing at all.

const CONFIG_PATH = "user://wolfparkhud.json"
const DEFAULT_POS = Vector2(16, 52)
const FEED_DEBOUNCE_MS = 1500
const METEOR_DEBOUNCE_MS = 3000

var steam = null
var layer
var panel
var grip
var coin_label
var feed_button
var meteor_button

var active = false
var coins = 0
var feeds = 0
var meteor_cost = 10
var feed_ready_at = 0
var meteor_ready_at = 0

var poll_timer = 0.0
var dragging = false
var drag_offset = Vector2()
var theme_applied = false


func _ready():
	if Engine.has_singleton("Steam"):
		steam = Engine.get_singleton("Steam")
	_build_ui()
	_load_config()


func _build_ui():
	layer = CanvasLayer.new()
	layer.layer = 3
	add_child(layer)

	panel = PanelContainer.new()
	panel.visible = false
	panel.rect_position = DEFAULT_POS
	panel.mouse_filter = Control.MOUSE_FILTER_STOP
	layer.add_child(panel)

	var margin = MarginContainer.new()
	margin.add_constant_override("margin_left", 10)
	margin.add_constant_override("margin_right", 10)
	margin.add_constant_override("margin_top", 6)
	margin.add_constant_override("margin_bottom", 6)
	margin.mouse_filter = Control.MOUSE_FILTER_IGNORE
	panel.add_child(margin)

	var row = HBoxContainer.new()
	row.add_constant_override("separation", 10)
	row.mouse_filter = Control.MOUSE_FILTER_IGNORE
	margin.add_child(row)

	grip = Label.new()
	grip.text = "::"
	grip.mouse_filter = Control.MOUSE_FILTER_STOP
	grip.mouse_default_cursor_shape = Control.CURSOR_MOVE
	grip.hint_tooltip = "Drag to move"
	grip.connect("gui_input", self, "_on_grip_input")
	row.add_child(grip)

	coin_label = Label.new()
	coin_label.text = "Coins: 0"
	coin_label.add_color_override("font_color", Color(1.0, 0.85, 0.4))
	coin_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	row.add_child(coin_label)

	feed_button = Button.new()
	feed_button.text = "Feed Wolves"
	feed_button.focus_mode = Control.FOCUS_NONE
	feed_button.connect("pressed", self, "_on_feed")
	row.add_child(feed_button)

	meteor_button = Button.new()
	meteor_button.text = "Meteor (10)"
	meteor_button.focus_mode = Control.FOCUS_NONE
	meteor_button.connect("pressed", self, "_on_meteor")
	row.add_child(meteor_button)


func _process(delta):
	poll_timer += delta
	if poll_timer >= 0.5:
		poll_timer = 0.0
		_poll()
	_refresh()


# --- reading what the server tells us ---

func _poll():
	active = false
	if steam == null:
		return
	var net = get_node_or_null("/root/Network")
	if net == null:
		return
	if net.PLAYING_OFFLINE or net.STEAM_LOBBY_ID <= 0:
		return

	var lobby = net.STEAM_LOBBY_ID
	if str(steam.getLobbyData(lobby, "wp_hud")) != "1":
		return
	active = true

	var cost = int(str(steam.getLobbyData(lobby, "wp_meteor_cost")))
	if cost > 0:
		meteor_cost = cost

	var mine = str(steam.getLobbyData(lobby, "wp_p" + str(net.STEAM_ID)))
	var parts = mine.split(",")
	if parts.size() >= 2:
		coins = int(parts[0])
		feeds = int(parts[1])
	else:
		coins = 0
		feeds = 0


# --- showing it ---

func _get_hud():
	var players = get_tree().get_nodes_in_group("controlled_player")
	if players.empty():
		return null
	var hud = players[0].get("hud")
	if hud == null or not is_instance_valid(hud):
		return null
	return hud


func _refresh():
	if not active:
		panel.visible = false
		dragging = false
		return

	var hud = _get_hud()
	if hud == null:
		panel.visible = false
		return

	# only while nothing else is open, like the game's own hotbar
	var show = true
	if hud.get("menu") != 0:
		show = false
	if hud.get("hud_hidden") == true:
		show = false
	var options = get_node_or_null("/root/OptionsMenu")
	if options != null and options.get("open") == true:
		show = false
	panel.visible = show
	if not show:
		return

	if not theme_applied:
		theme_applied = true
		var main = hud.get_node_or_null("main")
		if main != null and main.theme != null:
			panel.theme = main.theme

	coin_label.text = "Coins: " + str(coins)
	var now = OS.get_ticks_msec()
	feed_button.disabled = now < feed_ready_at
	meteor_button.text = "Meteor (" + str(meteor_cost) + ")"
	meteor_button.disabled = coins < meteor_cost or now < meteor_ready_at


# --- the buttons ---

func _say(text):
	var hud = _get_hud()
	if hud != null and hud.has_method("_send_message"):
		hud._send_message(text)
		return
	var net = get_node_or_null("/root/Network")
	if net != null:
		net._send_message(text, "ffffff")


func _on_feed():
	feed_ready_at = OS.get_ticks_msec() + FEED_DEBOUNCE_MS
	_say("!feed")


func _on_meteor():
	meteor_ready_at = OS.get_ticks_msec() + METEOR_DEBOUNCE_MS
	_say("!meteor")


# --- moving the panel ---

func _on_grip_input(event):
	if event is InputEventMouseButton and event.button_index == BUTTON_LEFT:
		dragging = event.pressed
		if dragging:
			drag_offset = panel.rect_position - panel.get_global_mouse_position()
		else:
			_save_config()


func _input(event):
	if not dragging:
		return
	if event is InputEventMouseMotion:
		var pos = panel.get_global_mouse_position() + drag_offset
		var area = get_viewport().get_visible_rect().size
		pos.x = clamp(pos.x, 0, max(0, area.x - panel.rect_size.x))
		pos.y = clamp(pos.y, 0, max(0, area.y - panel.rect_size.y))
		panel.rect_position = pos
	elif event is InputEventMouseButton and event.button_index == BUTTON_LEFT and not event.pressed:
		dragging = false
		_save_config()


func _load_config():
	var file = File.new()
	if not file.file_exists(CONFIG_PATH):
		return
	if file.open(CONFIG_PATH, File.READ) != OK:
		return
	var data = parse_json(file.get_as_text())
	file.close()
	if typeof(data) == TYPE_DICTIONARY and data.has("x") and data.has("y"):
		panel.rect_position = Vector2(float(data["x"]), float(data["y"]))


func _save_config():
	var file = File.new()
	if file.open(CONFIG_PATH, File.WRITE) != OK:
		return
	file.store_string(to_json({"x": panel.rect_position.x, "y": panel.rect_position.y}))
	file.close()
