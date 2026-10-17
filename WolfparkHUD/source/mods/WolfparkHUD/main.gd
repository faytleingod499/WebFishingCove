extends Node

# Wolfpark HUD
# A small panel with your coin balance, a Feed Wolves button, a Buy menu and a gamble box.
# It only appears on servers that announce it (the Cove "Feed" plugin does this through the
# lobby data), on every other server this mod does nothing at all.
# On such a server, anything you type that starts with ! goes only to the server and is not shown in chat.

const CONFIG_PATH = "user://wolfparkhud.json"
const TITLE_ID = "title_lonely"
const TITLE_NAME = "Lonely"
const DEFAULT_POS = Vector2(16, 52)
const FEED_DEBOUNCE_MS = 1500
const BUY_DEBOUNCE_MS = 1500
const GAMBLE_DEBOUNCE_MS = 1500
const POPUP_ID = "WOLFPARK"

var steam = null
var layer
var panel
var grip
var coin_label
var feeds_label
var feed_button
var feed_bar
var buy_button
var board_button
var board_ready_at = 0
var title_label
var bet_field
var gamble_button
var shop_panel
var shop_box

var active = false
var coins = 0
var feeds = 0
var shop_items = []
var shop_signature = ""
var gamble_min = 0
var gamble_max = 0
var feed_ready_at = 0
var feed_cooldown_ms = 0
var feed_bar_start = 0
var feed_bar_end = 0
var last_feeds = -1
var feed_pressed_at = -100000
var buy_ready_at = 0
var gamble_ready_at = 0
var title_text = ""
var title_color = Color(0.0, 1.0, 1.0)
var chat_color = ""
var prev_title = ""
var chat_connected = false


var poll_timer = 0.0
var dragging = false
var drag_offset = Vector2()
var theme_applied = false
var locked_hud = null
var pending_unlock = false


func _ready():
	if Engine.has_singleton("Steam"):
		steam = Engine.get_singleton("Steam")
	_build_ui()
	_load_config()
	_connect_chat()
	_register_title()
	_revoke_title() # in case the game was closed while the title was still handed out


func _exit_tree():
	_unlock_hud()
	_revoke_title()


func _notification(what):
	if what == MainLoop.NOTIFICATION_WM_QUIT_REQUEST:
		_revoke_title()


func _build_ui():
	layer = CanvasLayer.new()
	layer.layer = 3
	add_child(layer)

	panel = PanelContainer.new()
	panel.visible = false
	panel.rect_position = DEFAULT_POS
	panel.mouse_filter = Control.MOUSE_FILTER_STOP
	panel.add_stylebox_override("panel", _glass(Color(0.06, 0.12, 0.18, 0.55), Color(0.55, 1.0, 1.0, 0.45), 10, 6))
	layer.add_child(panel)

	var margin = MarginContainer.new()
	margin.add_constant_override("margin_left", 10)
	margin.add_constant_override("margin_right", 10)
	margin.add_constant_override("margin_top", 6)
	margin.add_constant_override("margin_bottom", 6)
	margin.mouse_filter = Control.MOUSE_FILTER_IGNORE
	panel.add_child(margin)

	var column = VBoxContainer.new()
	column.add_constant_override("separation", 4)
	column.mouse_filter = Control.MOUSE_FILTER_IGNORE
	margin.add_child(column)

	# top: the drag grip, the title on the left, the coins and the times fed on the right
	var row = HBoxContainer.new()
	row.add_constant_override("separation", 10)
	row.mouse_filter = Control.MOUSE_FILTER_IGNORE
	column.add_child(row)

	grip = Label.new()
	grip.text = "::"
	grip.mouse_filter = Control.MOUSE_FILTER_STOP
	grip.mouse_default_cursor_shape = Control.CURSOR_MOVE
	grip.connect("gui_input", self, "_on_grip_input")
	row.add_child(grip)
	_add_tooltip(grip, "Move", "Drag to move this window")

	# the title, coins and fed count sit centred in the rest of the row
	var info = HBoxContainer.new()
	info.alignment = BoxContainer.ALIGN_CENTER
	info.add_constant_override("separation", 10)
	info.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	info.mouse_filter = Control.MOUSE_FILTER_IGNORE
	row.add_child(info)

	title_label = Label.new()
	title_label.visible = false
	title_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	info.add_child(title_label)

	coin_label = Label.new()
	coin_label.text = "Coins: 0"
	coin_label.add_color_override("font_color", Color(1.0, 0.85, 0.4))
	coin_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	info.add_child(coin_label)

	feeds_label = Label.new()
	feeds_label.text = "Fed: 0"
	feeds_label.add_color_override("font_color", Color(0.75, 0.95, 1.0))
	feeds_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	info.add_child(feeds_label)

	# middle: leaderboard, feed and buy
	var button_row = HBoxContainer.new()
	button_row.add_constant_override("separation", 8)
	button_row.mouse_filter = Control.MOUSE_FILTER_IGNORE
	column.add_child(button_row)

	# the leaderboard icon: a little clipboard that runs !wolves
	board_button = Button.new()
	board_button.focus_mode = Control.FOCUS_NONE
	board_button.rect_min_size = Vector2(26, 22)
	board_button.connect("pressed", self, "_on_board")
	board_button.connect("draw", self, "_draw_clipboard")
	_glass_button(board_button)
	button_row.add_child(board_button)
	_add_tooltip(board_button, "Leaderboard", "Who has fed the wolves the most")


	feed_button = Button.new()
	feed_button.text = "Feed Wolves"
	feed_button.focus_mode = Control.FOCUS_NONE
	feed_button.connect("pressed", self, "_on_feed")
	_glass_button(feed_button)
	# while the wolves are full the button is clearly grey, the colour then comes back from left to right
	feed_button.add_stylebox_override("disabled", _glass(Color(0.45, 0.45, 0.45, 0.30), Color(0.7, 0.7, 0.7, 0.3), 8, 0))
	button_row.add_child(feed_button)

	# the cooldown bar: the button's colour, sliding back in behind the text as the cooldown runs down
	feed_bar = ColorRect.new()
	feed_bar.color = Color(0.3, 1.0, 1.0, 0.38)
	feed_bar.show_behind_parent = true
	feed_bar.mouse_filter = Control.MOUSE_FILTER_IGNORE
	feed_bar.visible = false
	feed_button.add_child(feed_bar)

	buy_button = Button.new()
	buy_button.text = "Buy"
	buy_button.focus_mode = Control.FOCUS_NONE
	buy_button.connect("pressed", self, "_on_buy_toggle")
	_glass_button(buy_button)
	button_row.add_child(buy_button)

	bet_field = LineEdit.new()
	bet_field.placeholder_text = "coins"
	bet_field.max_length = 6
	bet_field.rect_min_size = Vector2(70, 0)
	bet_field.caret_blink = true
	bet_field.add_stylebox_override("normal", _glass(Color(0, 0, 0, 0.30), Color(0.55, 1.0, 1.0, 0.35), 8, 0))
	bet_field.add_stylebox_override("focus", _glass(Color(0, 0, 0, 0.40), Color(0.8, 1.0, 1.0, 0.9), 8, 0))
	bet_field.add_color_override("font_color", Color(1, 1, 1))
	bet_field.connect("text_changed", self, "_on_bet_changed")
	bet_field.connect("focus_entered", self, "_on_bet_focus")
	bet_field.connect("focus_exited", self, "_on_bet_unfocus")
	bet_field.connect("text_entered", self, "_on_bet_entered")
	gamble_button = Button.new()
	gamble_button.text = "Gamble"
	gamble_button.focus_mode = Control.FOCUS_NONE
	gamble_button.connect("pressed", self, "_on_gamble")
	_glass_button(gamble_button)
	button_row.add_child(gamble_button)
	button_row.add_child(bet_field)

	# the list that opens under the panel with the Buy button
	shop_panel = PanelContainer.new()
	shop_panel.visible = false
	shop_panel.mouse_filter = Control.MOUSE_FILTER_STOP
	shop_panel.add_stylebox_override("panel", _glass(Color(0.06, 0.12, 0.18, 0.7), Color(0.55, 1.0, 1.0, 0.45), 10, 6))
	layer.add_child(shop_panel)

	var shop_margin = MarginContainer.new()
	shop_margin.add_constant_override("margin_left", 8)
	shop_margin.add_constant_override("margin_right", 8)
	shop_margin.add_constant_override("margin_top", 6)
	shop_margin.add_constant_override("margin_bottom", 6)
	shop_margin.mouse_filter = Control.MOUSE_FILTER_IGNORE
	shop_panel.add_child(shop_margin)

	shop_box = VBoxContainer.new()
	shop_box.add_constant_override("separation", 4)
	shop_box.mouse_filter = Control.MOUSE_FILTER_IGNORE
	shop_margin.add_child(shop_box)


# a see-through, softly outlined box
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


func _process(delta):
	poll_timer += delta
	if poll_timer >= 0.5:
		poll_timer = 0.0
		_poll()
		_sync_title()
	_refresh()
	# the game reads its keys before this node runs, so let go of its controls only after that
	if pending_unlock:
		pending_unlock = false
		if bet_field == null or not bet_field.has_focus():
			_unlock_hud()


# --- reading what the server tells us ---

func _poll():
	var was_active = active
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
	if not was_active:
		# just arrived on the server (or came back to it): forget the old feed count and cooldown
		last_feeds = -1
		feed_bar_end = 0
		feed_bar_start = 0
		feed_ready_at = 0

	title_text = str(steam.getLobbyData(lobby, "wp_title"))
	var color_text = str(steam.getLobbyData(lobby, "wp_title_color")).replace("#", "")
	title_color = Color(color_text) if color_text.length() == 6 else Color(0.0, 1.0, 1.0)

	chat_color = str(steam.getLobbyData(lobby, "wp_chat_color")).replace("#", "")
	if chat_color.length() != 6:
		chat_color = ""

	# what the Buy button lists, "id~Label~price;id~Label~price"
	var shop_text = str(steam.getLobbyData(lobby, "wp_shop"))
	if shop_text != shop_signature:
		shop_signature = shop_text
		shop_items = []
		for entry in shop_text.split(";", false):
			var bits = entry.split("~")
			if bits.size() >= 3:
				shop_items.append({"id": bits[0], "label": bits[1], "price": int(bits[2])})
		_rebuild_shop()

	feed_cooldown_ms = int(str(steam.getLobbyData(lobby, "wp_feed_cd"))) * 1000

	# the bet limits "min,max", empty when the server has no gambling
	var gamble_text = str(steam.getLobbyData(lobby, "wp_gamble")).split(",")
	if gamble_text.size() >= 2:
		gamble_min = int(gamble_text[0])
		gamble_max = int(gamble_text[1])
	else:
		gamble_min = 0
		gamble_max = 0

	# mine: "coins,feeds" (the server may add more after that, the mod ignores it)
	var mine = str(steam.getLobbyData(lobby, "wp_p" + str(net.STEAM_ID)))
	var parts = mine.split(",")
	if parts.size() >= 2:
		coins = int(parts[0])
		feeds = int(parts[1])
	else:
		coins = 0
		feeds = 0
	# a feed just counted (from the button or typed), so the wolves are full for the cooldown.
	# The first number we see for this player is only where they stood when they joined, never a feed,
	# except when this player pressed feed a moment ago (a brand new player's very first feed).
	var now = OS.get_ticks_msec()
	if parts.size() < 2:
		pass
	elif last_feeds == -1:
		last_feeds = feeds
		if feeds > 0 and now - feed_pressed_at < 6000:
			_start_feed_bar(now)
	elif feeds > last_feeds:
		last_feeds = feeds
		_start_feed_bar(now)
	elif feeds < last_feeds:
		last_feeds = feeds


func _start_feed_bar(now):
	if feed_cooldown_ms > 0:
		feed_bar_start = now
		feed_bar_end = now + feed_cooldown_ms


func _rebuild_shop():
	for child in shop_box.get_children():
		shop_box.remove_child(child)
		child.queue_free()
	for item in shop_items:
		var button = Button.new()
		button.focus_mode = Control.FOCUS_NONE
		button.align = Button.ALIGN_LEFT
		button.connect("pressed", self, "_on_buy_item", [item["id"]])
		_glass_button(button)
		button.set_meta("price", item["price"])
		button.set_meta("label", item["label"])
		shop_box.add_child(button)
	shop_panel.rect_size = Vector2(0, 0)


# --- the Lonely title ---
# A real title in the game's own list (Cosmetics > Misc). It is registered all the time, so the game can
# show it on other players, but only handed out to the player while they are on the server.
# Someone wearing it looks like a default character to players without this mod, so it is taken off
# again when they leave the server or close the game.

func _register_title():
	var globals = get_node_or_null("/root/Globals")
	if globals == null or globals.cosmetic_data.has(TITLE_ID):
		return
	var script = load("res://Resources/Scripts/cosmetic_resource.gd")
	if script == null:
		return
	var res = script.new()
	res.name = TITLE_NAME
	res.desc = "Only for Wolf Park."
	res.title = TITLE_NAME
	res.category = "title"
	res.cos_internal_id = 0
	res.in_rotation = false
	res.chest_reward = false
	globals.cosmetic_data[TITLE_ID] = {"file": res}


func _sync_title():
	if active:
		_grant_title()
	else:
		_revoke_title()


func _grant_title():
	var data = get_node_or_null("/root/PlayerData")
	if data == null:
		return
	_register_title()
	if not data.cosmetics_unlocked.has(TITLE_ID):
		data.cosmetics_unlocked.append(TITLE_ID)
	var worn = data.cosmetics_equipped.get("title", "")
	if worn != TITLE_ID and worn != prev_title:
		prev_title = worn
		_save_config()


func _revoke_title():
	var data = get_node_or_null("/root/PlayerData")
	if data == null:
		return
	if data.cosmetics_unlocked.has(TITLE_ID):
		data.cosmetics_unlocked.erase(TITLE_ID)
	if data.cosmetics_equipped.get("title", "") == TITLE_ID:
		var globals = get_node_or_null("/root/Globals")
		var back = prev_title
		if back == "" or back == TITLE_ID or globals == null or not globals.cosmetic_data.has(back):
			back = "title_rank_1"
		data.cosmetics_equipped["title"] = back


# --- server messages in colour ---

func _connect_chat():
	var net = get_node_or_null("/root/Network")
	if net == null or chat_connected:
		return
	if net.has_signal("_chat_update"):
		net.connect("_chat_update", self, "_on_chat_update")
		chat_connected = true


# Every chat line a player writes carries their coloured name, so a global line without any colour tag
# comes from the server (or the game itself). Those get wrapped in the server colour.
func _on_chat_update():
	if not active or chat_color == "":
		return
	var net = get_node_or_null("/root/Network")
	if net == null:
		return
	var lines = net.GAMECHAT_COLLECTIONS
	var changed = false
	for i in range(lines.size()):
		var line = str(lines[i])
		if line.find("[color=") == -1 and line.strip_edges() != "":
			var body = line.substr(1) if line.begins_with("\n") else line
			lines[i] = "\n[color=#" + chat_color + "]" + body + "[/color]"
			changed = true
	if not changed:
		return
	var all = ""
	for line in lines:
		all += str(line)
	net.GAMECHAT = all
	var hud = _get_hud()
	if hud != null and hud.has_method("_chat_update"):
		hud._chat_update()


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
		shop_panel.visible = false
		dragging = false
		_unlock_hud()
		return

	var hud = _get_hud()
	if hud == null:
		panel.visible = false
		shop_panel.visible = false
		return

	# only while nothing else is open, like the game's own hotbar. Our own lock on the game
	# (while typing a bet) counts as a popup, so it has to be looked past.
	var show = true
	if hud.get("menu") != 0:
		show = false
	if hud.get("hud_hidden") == true:
		show = false
	var popups = hud.get("popups")
	if typeof(popups) == TYPE_ARRAY:
		for entry in popups:
			if entry != POPUP_ID:
				show = false
	var options = get_node_or_null("/root/OptionsMenu")
	if options != null and options.get("open") == true:
		show = false
	panel.visible = show
	if not show:
		shop_panel.visible = false
		_unlock_hud()
		return

	if not theme_applied:
		theme_applied = true
		var main = hud.get_node_or_null("main")
		if main != null and main.theme != null:
			panel.theme = main.theme
			shop_panel.theme = main.theme

	var now = OS.get_ticks_msec()
	title_label.visible = title_text != ""
	title_label.text = title_text
	title_label.add_color_override("font_color", title_color)
	coin_label.text = "Coins: " + str(coins)
	feeds_label.text = "Fed: " + str(feeds)
	feed_button.disabled = now < feed_ready_at or now < feed_bar_end
	if now < feed_bar_end and feed_bar_end > feed_bar_start:
		var progress = clamp(float(now - feed_bar_start) / float(feed_bar_end - feed_bar_start), 0.0, 1.0)
		feed_bar.visible = true
		feed_button.add_color_override("font_color_disabled", Color(1, 1, 1, 0.35 + 0.65 * progress))
		feed_bar.rect_position = Vector2(1, 1)
		feed_bar.rect_size = Vector2(max(0.0, (feed_button.rect_size.x - 2) * progress), max(0.0, feed_button.rect_size.y - 2))
	else:
		feed_bar.visible = false

	# the gamble row only exists when the server has gambling
	var gamble_on = gamble_max > 0
	bet_field.visible = gamble_on
	gamble_button.visible = gamble_on
	bet_field.placeholder_text = str(gamble_min) + "-" + str(gamble_max)
	gamble_button.disabled = now < gamble_ready_at or coins < max(gamble_min, 1)

	buy_button.visible = shop_items.size() > 0
	buy_button.disabled = now < buy_ready_at
	shop_panel.visible = shop_open and shop_items.size() > 0
	if shop_panel.visible:
		shop_panel.rect_position = panel.rect_position + Vector2(0, panel.rect_size.y + 4)
		for button in shop_box.get_children():
			var price = button.get_meta("price")
			button.text = str(button.get_meta("label")) + "  -  " + str(price)
			button.disabled = coins < price or now < buy_ready_at


# --- the buttons ---

var shop_open = false


# Sends a message that only the server gets, in the same shape as a normal chat message so Cove reads it
# as the command it is. Nothing is shown in your chat and nobody else receives it.
func _say(text):
	if text.strip_edges().to_lower() == "!feed":
		feed_pressed_at = OS.get_ticks_msec()
	if _send_hidden(text):
		return
	# could not do it quietly, so say it in chat like normal
	var hud = _get_hud()
	if hud != null and hud.has_method("_send_message"):
		hud._send_message(text)


func _send_hidden(text):
	if text.strip_edges().to_lower() == "!feed":
		feed_pressed_at = OS.get_ticks_msec()
	if steam == null:
		return false
	var net = get_node_or_null("/root/Network")
	var data = get_node_or_null("/root/PlayerData")
	var globals = get_node_or_null("/root/Globals")
	if net == null or data == null or globals == null:
		return false
	if net.STEAM_LOBBY_ID <= 0 or net.PLAYING_OFFLINE:
		return false

	var host = int(steam.getLobbyOwner(net.STEAM_LOBBY_ID))
	if host <= 0 or host == net.STEAM_ID:
		return false

	var color = "ffffff"
	var equipped = data.cosmetics_equipped
	if typeof(equipped) == TYPE_DICTIONARY and equipped.has("primary_color") and globals.cosmetic_data.has(equipped["primary_color"]):
		var base = globals.cosmetic_data[equipped["primary_color"]]["file"].main_color
		color = str((base * Color(0.95, 0.9, 0.9)).to_html())

	net._send_P2P_Packet({
		"type": "message",
		"message": "%u: " + text,
		"color": color,
		"local": false,
		"position": net.MESSAGE_ORIGIN,
		"zone": net.MESSAGE_ZONE,
		"zone_owner": data.player_saved_zone_owner
	}, str(host), 2, net.CHANNELS.GAME_STATE)
	return true


func _on_feed():
	feed_pressed_at = OS.get_ticks_msec()
	feed_ready_at = OS.get_ticks_msec() + FEED_DEBOUNCE_MS
	_say("!feed")


# the game's own hover box (the one the backpack uses), so it looks and behaves like the rest of the game
func _add_tooltip(control, header, body):
	var scene = load("res://Scenes/Singletons/Tooltips/tooltip_node.tscn")
	if scene == null or get_node_or_null("/root/Tooltip") == null:
		control.hint_tooltip = header # no game tooltips here, the plain one will do
		return
	var tip = scene.instance()
	tip.header = "[color=#6a4420]" + header + "[/color]"
	tip.body = body
	tip.mouse_filter = Control.MOUSE_FILTER_PASS
	control.add_child(tip)


func _on_board():
	var now = OS.get_ticks_msec()
	if now < board_ready_at:
		return
	board_ready_at = now + 1500
	_say("!wolves")


# the clipboard is drawn, so it does not depend on any font having an icon for it
func _draw_clipboard():
	var b = board_button
	var c = Color(1, 1, 1, 0.95) if not b.disabled else Color(1, 1, 1, 0.4)
	var o = (b.rect_size - Vector2(12, 14)) / 2.0
	o = Vector2(floor(o.x), floor(o.y))
	b.draw_rect(Rect2(o + Vector2(0, 2), Vector2(12, 12)), c, false)
	b.draw_rect(Rect2(o + Vector2(3, 0), Vector2(6, 3)), c, true)
	b.draw_rect(Rect2(o + Vector2(3, 6), Vector2(6, 1)), c, true)
	b.draw_rect(Rect2(o + Vector2(3, 9), Vector2(6, 1)), c, true)


func _on_buy_toggle():
	shop_open = not shop_open


func _on_buy_item(id):
	buy_ready_at = OS.get_ticks_msec() + BUY_DEBOUNCE_MS
	shop_open = false
	_say("!buy " + str(id))


func _on_gamble():
	_do_gamble()


func _on_bet_entered(text):
	_do_gamble()
	bet_field.release_focus()


func _do_gamble():
	# the server explains the limits or missing coins in chat
	var amount = int(bet_field.text)
	if amount <= 0:
		return
	gamble_ready_at = OS.get_ticks_msec() + GAMBLE_DEBOUNCE_MS
	_say("!gamble " + str(amount))


# only digits in the bet box
func _on_bet_changed(text):
	var clean = ""
	for i in range(text.length()):
		var c = text[i]
		if c >= "0" and c <= "9":
			clean += c
	if clean != text:
		var caret = bet_field.caret_position
		bet_field.text = clean
		bet_field.caret_position = min(caret, clean.length())


# While the box has the keyboard the game must not react to the keys: the game counts a "popup" as
# something that takes over the controls, which is what stops digits from changing the hotbar
func _on_bet_focus():
	var hud = _get_hud()
	if hud == null:
		return
	var popups = hud.get("popups")
	if typeof(popups) == TYPE_ARRAY and not popups.has(POPUP_ID):
		popups.append(POPUP_ID)
	locked_hud = hud


func _on_bet_unfocus():
	pending_unlock = true


func _unlock_hud():
	if locked_hud != null and is_instance_valid(locked_hud):
		var popups = locked_hud.get("popups")
		if typeof(popups) == TYPE_ARRAY:
			popups.erase(POPUP_ID)
	locked_hud = null


# --- typed commands and moving the panel ---

# typed commands: when Enter is pressed with a ! message in the chat box, take it before the game
# sends it, send it to the server only and empty the box so the game has nothing left to say
func _input(event):
	if dragging:
		_drag(event)
		return

	# Escape or a click somewhere else lets go of the bet box
	if bet_field != null and bet_field.has_focus():
		if event is InputEventKey and event.pressed and event.scancode == KEY_ESCAPE:
			bet_field.release_focus()
			get_tree().set_input_as_handled()
			return
		if event is InputEventMouseButton and event.pressed:
			if not bet_field.get_global_rect().has_point(event.position):
				bet_field.release_focus()
		return

	if not active or not event.is_action_pressed("chat_enter"):
		return
	var hud = _get_hud()
	if hud == null or hud.get("using_chat") != true:
		return
	var box = hud.get("chat")
	if box == null:
		return
	var typed = str(box.text).strip_edges()
	if typed.begins_with("!"):
		if _send_hidden(typed):
			box.text = ""


func _on_grip_input(event):
	if event is InputEventMouseButton and event.button_index == BUTTON_LEFT:
		dragging = event.pressed
		if dragging:
			drag_offset = panel.rect_position - panel.get_global_mouse_position()
		else:
			_save_config()


func _drag(event):
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
	if typeof(data) == TYPE_DICTIONARY and data.has("prev_title"):
		prev_title = str(data["prev_title"])


func _save_config():
	var file = File.new()
	if file.open(CONFIG_PATH, File.WRITE) != OK:
		return
	file.store_string(to_json({"x": panel.rect_position.x, "y": panel.rect_position.y, "prev_title": prev_title}))
	file.close()
