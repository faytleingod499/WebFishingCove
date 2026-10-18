extends Area

# The spot on another player (who also has HugsAndMore) that the game's own "interact" key finds:
# walking up to them shows the game's usual prompt, "Hug <name>", and pressing it asks them for a hug.

var text = "Hug"
var target_id = 0
var mod = null


func _ready():
	add_to_group("interactable")


func _activate(actor):
	if mod != null and is_instance_valid(mod):
		mod._on_area_activated(target_id, actor)
