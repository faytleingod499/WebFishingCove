extends Node

# Tail Size Fix
# Makes every player's tail grow and shrink with them.
#
# The game's tail script (tail_root.gd) lifts the swinging part of the tail ("swish_point") out of the
# body with set_as_toplevel, so it can swing freely. That also cuts it off from the player's size:
# grow or shrink and everything scales with you except the tail. Every physics frame this gives each
# player's swish_point the same size as the bit of the tail it hangs from ("rot_point"), which does
# still follow the player. It only sets the size; the game keeps doing the swinging and wagging.
#
# Works for every player in the lobby, whether they have the mod or not, and for any size, including
# sizes set by other mods. Nothing is sent over the network.

const VERSION = "1.0.0"
const TAIL_PATH = "body/player_body/Armature/Skeleton/tail/holder/tail"


func _ready():
	pause_mode = PAUSE_MODE_PROCESS
	print("[TailSizeFix] ", VERSION, " loaded")


func _physics_process(_delta):
	for player in get_tree().get_nodes_in_group("player"):
		_fit_tail(player)


func _fit_tail(player):
	if not (player is Spatial) or not player.is_inside_tree():
		return
	var tail = player.get_node_or_null(TAIL_PATH)
	if tail == null:
		return
	var rot = tail.get_node_or_null("rot_point")
	var swish = tail.get_node_or_null("rot_point/swish_point")
	if rot == null or swish == null or not (swish is Spatial):
		return
	var size = rot.global_transform.basis.get_scale()
	if size.x <= 0.0 or size.y <= 0.0 or size.z <= 0.0:
		return
	if not swish.scale.is_equal_approx(size):
		swish.scale = size
