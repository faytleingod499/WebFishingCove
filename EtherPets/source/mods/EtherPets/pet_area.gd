extends Area

# The spot on a wolf the game's own "interact" key finds: walking up to a wolf shows the game's
# usual prompt, and pressing it asks the Wolf Pets mod to start petting.

var text = "Pet wolf"
var pet_key = null
var mod = null


func _ready():
	add_to_group("interactable")


func _activate(actor):
	if mod != null and is_instance_valid(mod):
		mod._on_pet_activated(pet_key, actor)
